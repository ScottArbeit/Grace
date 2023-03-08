# Grace Version Control System

grace _(n)_ -

1. elegance and beauty of movement, form, or expression
2. a pleasing or charming quality
3. goodwill or favor
4. a sense of propriety and consideration for others [^grace]

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Welcome to Grace.
Grace is a **new**, **modern**, **cloud-native** **version control system**.

Grace is **easy to use**, **easy to understand**, and **consistently fast**. And it's **powerful**, ready to handle large repositories and large file sizes.

Grace Server **scales up** by running on Kubernetes and **massive PaaS services** from large cloud providers.

Grace Client runs in the background, making it **ambient**, **faster**, and **more valuable to your everyday work** as a developer.

Grace **connects you with others** working in your repository, **across the globe**, **in real-time**, enabling **new experiences** and **new ways of sharing**.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

to be clear:
> Grace is **new** 🧑🏼‍🔬 and **alpha-level** right now. 🔥🧯 The parts that **are** implemented work, but there's much more to do. Grace is not ready to be relied on as a production version control system yet.  It should not be used for anything other than its own design, development and testing, and for feedback.[^git]

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)
![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)
### Hi friends from On .NET Live!

_08-Mar-2023_

Thanks for stopping by. I really do appreciate it.

I owe you some videos with the demos of Grace, and stuff in Grace's code, that I wanted to show you.

To make those videos, I just bought a new computer (yeah, yeah...), with a serious GPU (of course...), and I'm learning a bit about recording and editing so they don't suck. I expect to publish them later this month. I appreciate your patience.

In the meantime, please feel free to comment or ask questions in the Discussions here. I'd love to know what you're curious about.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)
![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)


## FAQ

For a list of (mostly imagined) frequently asked questions, please see the [Frequently Asked Questions](docs/Frequently%20asked%20questions.md) page.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Design and Motivations

If you'd like to read about some of the design thinking and motivations behind Grace - topics like UX, performance, scalability, monorepos, Git, why F#, and more - please read [Design and Motivations](docs/Design%20and%20Motivations.md).

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Vision

#### A lot of this remains to be built, but here's the vision for Grace v1.0:

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### No more fear

Stop being afraid of your version control system, and start _enjoying_ it instead.

Grace is easy-to-use, easy-to-understand, and fast, with cool new features that let version control fade into the background while you're working, and help you remember where you were when you get interrupted.

There are fewer concepts to understand in Grace, so learning it is easy, and understanding what it's doing is simple. There's a simple-to-understand grammar, built-in aliases for common gestures, and common-sense defaults.

Grace is powerful source control, minus the fear.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Yes, it's fast

Great UX requires great speed. And Grace is fast.

Grace Server is designed to run on fast, cloud-based PaaS services for incredible scale and performance. Grace uses virtual actors as networked, in-memory data caches to maximize performance.

Grace CLI adds just milliseconds to the server response time for each command.

Grace's GUI apps will be platform-native, with all of the performance and stick-to-your-finger-ness that native apps have always had.

Grace Server can even precompute views and projections that you're likely to want, like diffs and directory contents, and garbage-collect them when they're no longer needed.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### grace watch

Grace CLI includes a command - `grace watch` - that watches your working directory for changes, and keeps a live connection to Grace Server, connecting you in real-time to your team around the world.

`grace watch` takes action for you, whether that's automatically uploading and downloading new file versions, processing notifications from other repo users, or running custom actions that you create.

And don't worry about having yet-another application running in the background. `grace watch` is small and very quiet when nothing is going on.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Commits... and saves, checkpoints, and promotions

In Git, `commit` is an overloaded concept. It can mean:

- "I'm partially done" - local commit after each unit of work is done
- "I'm really done" - ready for the pull request
- "Merge" - merges are called `commits`, even if they're not.

And then you get the "squash" vs. "don't squash" debate. Sigh.

Grace simplifies this by breaking these usages out into their own gestures and events:

- `grace checkpoint` - this means "I'm partially done", for you to keep track of your own progress
- `grace commit` - this is "I'm really done" or "This version is a candidate for promotion"; you'd use a commit for a PR
- `grace promote` - this is sort-of a merge; it's what you'd get after approving a PR, which Grace calls a  _promotion request_[^pr]

and, introducing:

- `grace save` - this is used by `grace watch` for saving versions of files between checkpoints, because...

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Every save is uploaded, automatically

By default, `grace watch` uploads new file versions after every save-on-disk, along with a new snapshot of the entire directory structure of the repo, including new SHA-256 hashes.

It happens so quickly you don't even notice it.

And it gives you some very cool things, like:

- **file-level undo** for as far back as your repository allows,
- **very fast** `grace checkpoint`, `grace commit`, and `grace promote` commands, and,
- a **Version History view** that will let you to flip through your versions, helping you get remember where you were when you get interrupted, and enabling easy, instant restoration of any of your past changes.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Every change recorded

Grace is event-sourced. That means that everything that changes the state of the repository - every `save` and `commit`, every `branch name change`, every _everything_, is stored as a separate event.

As they're handled, they're sent to an event processor, which can log them in your choice of format and system. You can see the _who / what / where / when_ of everything that happens in your repository, using your favorite event analytics and stream analytics tools, and even set up custom auditing and automation based on those events.[^stream]

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Live, two-way client-server communication

When running `grace watch`, Grace uses [SignalR](https://dotnet.microsoft.com/en-us/apps/aspnet/signalr) to create a live, two-way communication channel between client and server.

This connection allows Grace to do All The Cool Things. Things like connecting you in real-time to everyone else working in your repository. Things like auto-rebasing. Things like watching for events in your repository, notifying you when you want to be notified, and running custom local actions if you want.

Imagine: there's a promotion to `main`, your branch gets auto-rebased on those latest changes, and then your local unit test suite gets run automatically so you immediately know if there's a problem.

Grace lets you share your code with team members effortlessly, around the world, for those times when you need another set of eyes on it, or just want to show them something cool.

Auto-rebasing keeps you up-to-date and ready to go, because...

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Grace reduces merge conflicts

Merge conflicts suck. Finding out that you have one, when you thought you were already done with your work, is one of the most anxiety-inducing parts of using source control. Grace helps you eliminate them by keeping your branch up-to-date.

When your parent branch gets updated, by default, `grace watch` will auto-rebase your branch on those changes, so you're always coding against the latest version that you'll have to promote to.

Almost all of the time, when you rebase, nothing bad happens. You don't even notice it. The rest of the time, auto-rebase lets you find out right away, fix it while you're in flow, and skip the conflict later.

Let's shift left on promotion conflicts. Grace can't eliminate all of them, but it should reduce how often they happen.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Personal branches, not forks

With Grace, there's no need for forking entire repositories just to make contributions. In open-source repos, you'll just create a personal branch against the repo.

You'll own your personal branch, and you can make it public or private. When your change is ready you can submit PR's to get your personal branch's version promoted to a parent branch in the repo.

This is how I expect a large, open-source project in Grace to be: dozens of contributors, each with personal branches, working on a public project that remains securely controlled with ACL's. Everyone auto-rebased with every update to their parent branch, so there are no surprises later. No networks of forks to manage, no multiple entire copies of the repo. Just individuals working on the same repo, securely, together.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Simplified branching strategy

Grace's default branching strategy is called _single-step_ and is designed to help reduce merge conflicts, and to make it easier to work on and promote code to shipping branches (like `main`).

Single-step branching is, we hope, both easy-to-use and powerful enough to be all that you need to run your projects.

There's a [separate page](docs/Branching%20strategy.md) that describes it in more detail.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Run large repositories

Got a lot of files? a lot of users? a lot of versions?

In short... got a monorepo?

No problem, Grace is ready for it. So far, Grace has been tested on repositories as large as 100,000 files with 15,000 directories, with excellent performance (if you're running `grace watch`).

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Store large files

Grace has no problem storing large files. Really large files. It's been tested with 10GB files - not that I think files that large belong in version control - and it should handle even larger files well.

Grace will let you specify how to handle those files, like only downloading them for the Design department, but not for Engineering. It's up to you.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Native GUI

Yeah, I said it.

Grace will have a native GUI app for Windows, Mac, Android, and iOS. (And probably Linux.)

Take Grace with you wherever you go. Merge conflict UI, Version History view, repository browsing, current status and more... all running at full native speed on your devices.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Web UI

Shipping a native app doesn't mean that we don't also need a great web UI.

Sometimes the best way to share information is using a URL.

CLI + Web UI + GUI... use Grace the way you want to.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### ACL's, down to the file level

Grace goes way beyond just repository-level permissions. Grace uses OpenID and OAuth2 to integrate with your AuthN and AuthZ providers. Want to lock down specific paths in your repository to specific users and groups? With Grace, you'll be able to.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### No stashing

Because `grace watch` automatically uploads new file versions after every save, and commands like `grace switch` ensure that everything in your working directory is preserved before switching to another branch or reference, there's no need to stash anything. Just `switch` and go.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Branch-level control of reference types

Plan your branching strategy in greater detail than ever before. Grace lets you decide which kinds of references are enabled in each branch. 

For instance, `main` might enable promotions and tags, and disable commits, checkpoints, and saves.

Ordinary user branches might disable promotions, but enable everything else.

It's up to you.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Ephemeral checkpoints and saves

Checkpoints and saves are features to help **you** be more productive, but we don't need to keep them forever. Grace allows you to control how long they're kept are kept in each repository. 72 hours? One week? A month? Or maybe just "keep the last 1,000 saves". It's up to you.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Delete versions if you need to

Sometimes you need to delete a version, whether it's because a secret or password was accidentally checked in, or because of some other security issue. With the right permissions, you'll be able to remove bad versions of your code - with a permanent reference that says that you did.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Pre-rendered diffs

Every time you run `grace promote`, `grace commit`, or `grace checkpoint`, and every time `grace watch` uploads a new version of a file, Grace Server can optionally pre-render diffs for you to see, either in the CLI or in the Version History view. This makes seeing your ongoing changes in the Version History view instantaneous.

They're automatically aged out and deleted after a configurable length of time, so they don't just sit there wasting resources forever.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Optional file locking

If your repository includes large binary files, like edited video, graphics, or game artifacts, you need to make sure that only one person at a time edits each file. Grace will support optional file locking at the directory level to ensure that you don't have to re-do work.

(Don't worry... if you don't need file locking, you don't have to turn it on.)

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### A simple Web API

Grace Server is a modern Web API application. Like Grace's CLI, the Web API is easy to use, and easy to understand.

Grace ships with a .NET SDK, which is simply a projection of the Web API into .NET (and which Grace itself uses). SDK's for other platforms are welcomed as community contributions.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### SHA-256 hashes

Grace uses SHA-256 hashes to verify that the files you uploaded, and the directory versions that went with them, are exactly the ones that get retrieved by clients. Grace will include a command to verify the SHA-256 hashes of all downloaded files and directory versions.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Local file cache

Grace repositories have a local cache of file versions that have been uploaded and downloaded. When running `grace watch`, Grace can download new file versions from multiple branches in the background so your `grace switch` commands run nearly instantly.

The local file cache is pruned regularly.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### 2048 characters

When you're running `grace promote/commit/checkpoint/save/tag -m <some message>`, the _\<some message\>_ part can be up to 2048 characters.

50 characters... I don't think so. Feel free to share details. The person you help might be future you.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Import from Git / Export to Git

Yes, we know... it's hard to let go. Grace will perform an initial import from a Git repo, and will export to a Git repo.[^gitexport]

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Operations (if you're thinking about hosting your own server)

Grace Server is a modern, cloud-native Web API application. It will ship in a container on Docker Hub. (Of course.)

Grace Server is designed to be easy to deploy and operate. It runs on your choice of dozens of cloud-native databases, components and services, using [Dapr](https://dapr.io), making it flexible and inherently scalable. Grace Server is stateless and scales up and down well using basic [KEDA](https://keda.sh/) counters.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Project structure

Grace is written primarily in [F#](https://learn.microsoft.com/en-us/dotnet/fsharp/what-is-fsharp), and is organized into nine .NET projects.

- **Grace.CLI** is the command-line interface for Grace.
- **Grace.Server** is an ASP.NET Core project that defines the Web API for Grace.
- **Grace.SDK** is a .NET class library that is a platform-specific projection of the Web API; it is used by Grace.CLI and wraps the HTTPS calls to Grace Server.
- **Grace.Actors** holds the code for the Actors in the system; it is used exclusively by Grace.Server.
- **Grace.Shared** is where all common code goes; it is used by all of the other **Grace.*** projects.
- _(future)_ **Grace.MAUI** will contain a native GUI for Grace for Windows, Android, MacOS, and iOS (and hopefully Linux).
- _(future)_ **Grace.Blazor** will be an ASP.NET Core project containing a web UI for Grace.
- **CosmosJsonSerializer** is a custom JSON serializer class, used when deploying Grace with Azure Cosmos DB.

An additional project, **Grace.Load**, is an experiment to create a load test for Grace.Server.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Green.svg)

### Project diagram

> Solid lines indicate a .NET project reference. Dotted lines indicate network requests.

```mermaid
flowchart LR
    subgraph Server
    Grace.Server-->Grace.Actors
    Grace.Server-->CosmosJsonSerializer
    end

    subgraph Clients
    Grace.CLI-->Grace.SDK
    Grace.SDK-.->|HTTPS|Grace.Server
    MAUI["(future) Grace.MAUI"]-->|"(just started)"|Grace.SDK
    Blazor["(future) Grace.Blazor"]-->|"(not yet started)"|Grace.SDK
    end

    subgraph Utilities
    Grace.Load-->Grace.SDK
    end

    subgraph Tests
    Grace.Server.Tests-->Grace.Server
    end

    Grace.Actors-->Grace.Shared
    Grace.Server-->Grace.Shared
    Grace.CLI-->Grace.Shared
    Grace.SDK-->Grace.Shared
    MAUI["(future) Grace.MAUI"]-->|"(just started)"|Grace.Shared
    Blazor["(future) Grace.Blazor"]-->|"(not yet started)"|Grace.Shared
```

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Code of Conduct

Our Code of Conduct is available [here](code_of_conduct.md). The tl;dr is:

> **The name of this project is Grace**.
>
> Be **graceful** in your interactions.
>
> Give **grace** to everyone participating with us.
>
> Create something together that embodies **grace** in its design and form.
>
> When in doubt, **_remember the name of the project._**

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Deployment

Grace Server will be shipped as a container, which will be made available on Docker Hub. Dapr's sidecar and actor placement processes are shipped as containers and are available on Docker Hub.

We intend to provide a Docker Compose template, as well as Kubernetes configuration for deployment, allowing for deployment to any major public cloud provider, as well as on-premises hardware.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

[^grace]: Definition excerpted from https://www.thefreedictionary.com/grace.

[^git]: Grace currently uses Git for its source control, and runs Grace in the same directory as a means of testing. Officially self-hosting Grace's source code on Grace will, of course, happen when it's safe to.

[^pr]: They're not really "pull requests" because there isn't a "pull" gesture in Grace. It's still a "PR", though.

[^stream]: One thing I'd like to do with the event log as a stream: detect invalid sequences and frequencies of events in Grace that would indicate bugs or attacks.

[^gitexport]: Grace will export the latest state of each branch into a `git bundle` file. Exporting an entire Grace repository, including history, to a Git repository probably won't be supported, but maybe someone else will write it. (There are, no doubt, a lot of edge cases to be found the hard way in that translation, and it's a very low priority item for me.) Live two-way synchronization between Grace and Git is a non-goal, for the same reason.
