# What `grace watch` does

## Introduction

One of the most important pieces of Grace is `grace watch`. `grace watch` is a background process that watches your working directory for changes, automatically uploads changes after every save-on-disk, and enables you to run local tasks in response to events in the repository, including auto-rebase.

Most Grace users will be programmers, and we're a more technical audience. We know that background processes can be used in ways that are helpful, harmful, or just wasteful. As someone asking you to run a background process, I have a special responsibility to be transparent with you about what `grace watch` does if you allow it to run. (Which you totally should.) I want you to have complete confidence that running `grace watch` is safe and trustworthy.

## No dark patterns

There are certain behaviors that some products have around their use of background processes and schedulers that I find offensive. One example is Adobe Creative Cloud. I'm calling them out, not because I think they're deliberately evil, or worse than anyone else, but because it's an example that happens to be here in front of me, and to illustrate a *kind* of thing that Grace doesn't do.

Adobe Creative Cloud really, really wants to make sure its [background processes](https://helpx.adobe.com/gr_en/x-productkb/global/adobe-background-processes.html) are always running. They use four different items to register them: one entry is added to the Windows Task Scheduler, one entry is added to start a process every time I log in, and two different services are configured to run as Windows Services that start before I even log in.

If all of that isn't annoying enough, those services are configured for `Automatic` start, not `Automatic (Delayed Start)`, which means that they start as soon as they can during boot, competing for system resources at the worst possible time, when they could easily wait a couple of minutes and run when there's less going on. Sigh.

So, if I don't want Adobe's background processes, I remove those four entries, and, hey, I'm good, right?

Nope. It seems like whenever I start an Adobe app, it recreates these entries, ensuring that if I'm going to use Adobe Creative Cloud, I have no choice but to put up with what they think should be running on my machine.

What are these processes doing? What information are they collecting? How is that information being sent, and to whom? Are there third-party processors involved? Can I block it? Can I use a GDPR Data Subject Request to see or delete the information that's being sent?

I have no idea, and the way they keep recreating these entries after I deliberately remove them doesn't build trust.

Unless I write a PowerShell script to automate removing Adobe's registry entries and services, and ending those processes if they're running, and schedule it to run frequently, there's nothing I can do.

So... this kind of thing... stuff like that... you know what I mean? ... `grace watch` doesn't do that.

Here's what it actually does.

## Grace Watch - Compute, I/O, and network usage

This is meant to be an exhaustive list of the things that `grace watch` does. If it's not on this list, `grace watch` doesn't do it. If you believe I've missed something, please start a Discussion in the repo; if there's something to add, we'll create an issue and update this page.

Of course, it's open-source, please feel free to examine [Watch.CLI.fs](https://github.com/ScottArbeit/Grace/blob/main/src/Grace.CLI/Command/Watch.CLI.fs).

- `grace watch` uses a .NET [`FileSystemWatcher()`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-8.0) to watch your working directory for all changes and updates.
- `grace watch` establishes a SignalR connection with Grace Server, and sends the BranchId of the parent branch. Grace Server then registers your connection in the correct notification groups.
- When it starts, it scans the working directory and all (not-ignored) subdirectories and files for changes since the last time the local Grace Status file was updated.
- When it starts, and at a couple of other times, it reads and deserializes the local Grace Status file. For small repos, it's well under 10K and is processed in about 1ms. The largest repositories I've tested had a ~53MB status file, and, if I recall correctly, reading and deserializing the data happened in low two-digit milliseconds on a four-year-old laptop.
  - `grace watch` doesn't keep the Grace Status file in memory while it's running. It's so fast to read and deserialize it when it's needed that we make the tradeoff to release the memory rather than hold it indefinitely, especially given that there will be many times that the user isn't coding and `grace watch` should have as small of a memory footprint as possible.
- When a new or updated file is detected, `grace watch` will:
  - Copy the file to your user temp directory, using a system-generated temporary file name.
  - Compute the SHA-256 hash of the file, using the algorithm described [here](How%20Grace%20computes%20the%20SHA-256%20value.md).
  - Rename the file, inserting the SHA-256 hash, and move it to the repository's `.grace/objects` directory.
  - Upload the file from `.grace/objects` to the Object Storage provider that the repository is configured to use.
  - Recompute the directory versions from the file that was updated up to the root directory.
  - Update the local Grace Status file with the new directory versions.
  - Upload those recomputed directory versions to Grace Server.
  - Create a Save reference by calling Grace Server's `/branch/createSave` endpoint.
- When a promotion event from your parent branch is sent to `grace watch` by the server, `grace watch` will run auto-rebase.
- Every 4.8 minutes, `grace watch` will recompute and rewrite the Grace interprocess-communication (IPC) file, which requires reading and deserializing the local Grace Status file. The size of the IPC file is under 1K for small repos, and scales with the number of directories in the repo. A repo with 275 directories would fit in a 10K IPC file, and a repo with 2,750 directories would fit in a 100K IPC file. They're usually very small.
  > Long story about why we rewrite the file: Imagine that you're at the command line, and you run `grace checkpoint -m ...`. That instance of Grace uses the existence of the IPC file as proof that `grace watch` is running in a separate process. `grace watch` writes the IPC file as soon as it starts, and, deletes it in a `try...finally` clause when it exits. In other words: in any normal exit, including exits caused by unhandled exceptions, the IPC file will be deleted when `grace watch` exits. However: it's possible that `grace watch` could be killed before it has a chance to execute that `finally` clause. For instance, in Windows, if I open Task Manager, right-click on the `grace watch` process, and hit `End Task`, the process dies immediately, and does not execute the `finally` clause. To ensure that there's not a stale IPC file laying around, Grace checks the value of the UpdatedAt field; if it's more than 5 minutes old, Grace will ignore the IPC file and assume that `grace watch` isn't running. So: _that's_ why the IPC file gets refreshed every 4.8 minutes: it resets the UpdatedAt field so the file stays under 5 minutes old.
- Once a minute, `grace watch` does the fullest of garbage collection:
  
  `GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)`
  
  This ensures that `grace watch` keeps the smallest possible memory footprint, and takes single-digit µs when there's nothing to collect.
  
  > The .NET Runtime has excellent heuristics for when to run GC, but the biggest factor is memory pressure. If the OS isn't signaling that there's memory pressure on the system, GC's don't happen much. With `grace watch` running on a developer box with many GB's of RAM available, it's likely that there won't be much memory pressure, and it would rarely perform garbage collection. `grace watch` might look like it's taking up a lot of memory (from doing things like auto-upload, auto-rebase, and updating the IPC file), but it would all be Gen 0 references, ready to be collected.
  >
  > Given that there will be many times that a user isn't working in a repository, releasing memory proactively is the right thing to do.

## Process Monitor: `grace watch` is very quiet
This is a Windows-specific story, but it illustrates what `grace watch` is doing, or _not_ doing, regardless of platform. Those of you familiar with Windows administration will be familiar with [Sysinternals Tools](https://learn.microsoft.com/en-us/sysinternals/), originally written by, and still partially maintained by, Microsoft Azure CTO Mark Russinovich. Before he was the CTO and Chief Architect of Azure, he was the Chief Architect of Windows, and he literally wrote the book _Windows Internals_, which is a great read if you're an OS nerd of any kind. Sysinternals Tools, after 20+ years, are still essential advanced tools to know on Windows.

One of the Sysinternals Tools is [Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon), which allows you to watch every file, networking, registry, and process/thread event happening in the system in real-time. Process Monitor has excellent filtering, and when using it just to observe `grace watch`, what I can tell you is: if nothing from the list above is happening at exactly that moment, `grace watch` does nothing that Process Monitor can detect. No file events, no network events, just sometimes a thread creation/deletion event, managed by the .NET Runtime and not controlled by `grace watch`.

I left it running for 20 minutes with Process Monitor; aside from the IPC file refreshes, `grace watch` showed no events at all.

I can't make it any quieter than that.

## Future items
### Running local tasks
`grace watch` is intended to be able to run local tasks in response to repository events, but, aside from `grace rebase`, that functionality hasn't been written yet. When it is, we'll document it and add it to the section above.

### Telemetry
`grace watch` does not currently collect or send any telemetry, but I intend to before Grace ships. There will be clearly-documented ways to turn it off, if you'd prefer, and proper GDPR (and related) handling in place. The intention of this telemetry is to understand usage patterns and errors in using Grace, and to then use that data to improve both functionality and performance.

For any of you who have used a telemetry provider – like Datadog, or Azure Monitor, or Application Insights, or any of a hundred others – to understand the usage of your own apps, you know what I mean. Detailed telemetry will never be kept longer than 30 days.