# Frequently Asked Questions

(_or, what I imagine they might be_)

For deeper answers to some of these, please read [Design and Motivations](Design%20and%20Motivations.md).

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Is Grace centralized or decentralized?

Grace is a centralized version control system (VCS).

## I thought centralized version control systems were really slow.

Well, that's not a question... but... yeah, in some ways, the older centralized VCS's could feel slower, and heavier, especially when dealing with branches.

Grace is not like that. Grace is new, modern, lightweight, and fast.

## Why did you create Grace?

In the 2022 StackOverflow Developer Survey, Git is at 93.87% adoption. Git has won, no doubt.

And there's sort-of nowhere for it to go but down.

I've been around long enough to see different technologies rise and fall. Some have shorter market cycles (web UI frameworks, for instance), and some have longer market cycles, like hierarchical -> relational -> No-SQL databases, or popular social media apps. I've seen technologies that had almost 95% market share, with very long cycles, like Windows and Windows Server, eventually lose market share for one reason or another.

Git is 17 years old now. It doesn't have the easiest UX, to say the least. Many projects are exploring version control right now to see where it might go next. Git won't stay near 95% forever. Nothing ever does.

Grace is my offering to that search for what's next. Grace's design is my attempt to bring ease-of-use into a corner of our world that hasn't had much of that lately, and to connect us together in a different way than ever before.

## I like the way Git does branches.

Again with the not-question... I do too. I think that the lightweight branching in Git is one of the major reasons that it won.

That's why I kept lightweight branching in Grace. Create and delete branches to your heart's content.

## What about when I'm disconnected?

Well, you won't be able to run many Grace commands. And you probably won't be able to do lots of other things that you usually do.

More and more of us rely on cloud services and connectivity to the Internet just to do our jobs. Think about this: if your Internet connection went down, could you continue to do your job as a developer, or would you have to stop? Some could keep working, but if you can't, not having a connection to your source control server is the least of your concerns compared to not having a connection to Azure or AWS or wherever your cloud stuff is... not to mention StackOverflow and your favorite search engine.

With the growth of satellite Internet, we're connecting more and more of the world at high-enough bandwidth to use centralized version control without issue. And I'm not designing for the 0.000001% "but I'm on a flight without Internet" scenario.

If being able to use local version control while you're not connected to the Internet is an important scenario for you, please use Git. It's great at that. I'm guessing that there's still a small - important, but small - percentage of programmers in the world that _really need_ that. For the vast majority of us, though, assuming a working Internet connection isn't a big deal anymore.

Anyway, Grace won't stop you from continuing to edit the local copies of your files that you already have. When your Internet connection resumes, `grace watch` will catch you up immediately.

## Have you thought about using blockchain to...

No. ????????????????? Just... no.

## Can Grace do live two-way synchronization with Git repositories?

No, it can't. Two-way synchronization is a non-goal.

One-time initial import from a Git repo will be supported, and point-in-time export to a `git bundle` file will be supported, but not continuous two-way synchronization.

Grace just has a fundamentally different design than Git. That's intentional. Two-way synchronization would involve a messy translation between what Git calls a _merge_ and what Grace calls a _promotion_, and I don't see a good way right now to handle that well without writing a _lot_ of code and handling a _lot_ of edge cases, and that's time better spent on everything else I need to do.

Also, I'm not sure that new version control systems need to sync with Git to catch on. Git didn't have two-way sync with any of the VCS's that we all migrated from, and it didn't stop us: we did the migrations and went on with our lives.

## What are the scalability limits for Grace?

### Answer #1

It depends on the PaaS services that Grace is deployed on. In general, Grace itself is really fast, and will take advantage of however fast the underlying services it depends on will run.

### Answer #2

I'm not sure, because I haven't really pushed the limits yet.

I _can_ tell you that I've tested repositories of up to 100,000 files and 15,000 directories, with Grace deployed using Azure CosmosDB and Azure Blob Storage. If `grace watch` is running, performance on those large repositories is really good, and similar to performance on medium-sized and even small repositories for most commands.

I've also tested individual file sizes up to 10GB. I'm not sure that 10GB files should fall under the purview of version control - they should probably be versioned blobs in an object storage service - but we'll see what happens. There will be a configurable size limit for each repository.

The stateless nature of Grace Server, and the use of the Actor Pattern, should allow for a significant number of concurrent users without too much hassle, but I haven't done that kind of scale testing yet. I expect that when I do, I'll find the Top 5 Stupid Things I Did and fix them, and then Grace should be able to handle thousands of transactions/second. We'll find out soon.

## What does Grace borrow from Git?

A lot of things.

Grace keeps the ephemeral working directory, and the idea of a `.grace/objects` directory for each repo.

Grace keeps the lightweight branching, so you can continue to create and discard branches just as quickly as we do in Git.

Grace borrows the use of SHA-256 values as the hash function to uniquely identify files and directories.

We even borrowed the algorithm to decide if a file is text or binary.[^binary]

And much more. Grace owes a debt of gratitude to Git and to its maintainers.

## What about the other new version control projects?

There are a few really good VCS projects going on right now. It's exciting to see.

I'm fortunate enough to know the folks involved in [Jujutsu](https://github.com/martinvonz/jj). They're doing incredible work.

I'm aware of [Pijul](https://pijul.org/). I see Meta has just announced [Sapling](https://sapling-scm.com/). I know there are others.

All I can say is: Grace has its own design philosophy, and its own perspective on what it should feel like to be using version control, and that's what I'm passionate about. I think of it as a friendly competition to see which one wins with the next generation of developers.

Let 1,000 flowers bloom. One of them will lead us to the future of version control.

## On _HN_, FOSSBro761 says: _"Grace \<something something\> M$ \<something\>..."_

Um, yeah, OK. Whatever.

Here's what actually happened:

Grace started as my personal 2021 pandemic lockdown side-project. I invented it. No one told me to, I just did. I chose .NET because it's what I know and trust. I chose F# because I wanted to think functionally as I explored.

I chose to do it at all because I realized that _something_ was eventually going to replace Git, and I had some opinions about what that should be, and about what direction we should take as an industry in UX for source control. The only way to communicate that effectively was to start writing code and see if I could build something worthwhile.

That's the origin story. Just a guy who had an idea he couldn't let go of, using good tools that he knows well, and a few tools that he's learning.

If you look at my GitHub or LinkedIn profile you'll see that I work for GitHub, which means that I work for Microsoft. With that said, Grace is a personal side-project. It's probably the case that I wouldn't have thought to start a new version control system without having been at GitHub, but now I've caught the version control bug. It's a fascinating area to work in, and one that will see exciting innovation in the coming years. Grace's existence does not imply anything at all about GitHub's continued, massive, ongoing support of Git, or about GitHub's future direction in source control. It's the usual "all opinions and work here are personal, and not endorsed by my employers..." stuff.

## How can I get involved?

Why, thank you for asking. ??????

Everything helps. Feel free to file an Issue in the repo. Please join us over in Discussions.

I'm working right now to get Grace in better shape for debugging for everyone. I confess the debug workflow is very much tailored to me and my local machine at the moment. I'm going to fix that, and when I do, I'll post instructions for how to write code for and debug Grace as easily as possible.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

[^binary]: The algorithm is: is there a 0x00 byte anywhere in the first 8,000 bytes? If there is, it's assumed to be a binary file. Otherwise, it's assumed to be a text file.
