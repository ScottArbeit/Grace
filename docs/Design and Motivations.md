# Design and Motivations

Hi, I'm Scott. I created Grace.

I'll use first-person in this document, not because I wish to imply that Grace is *my project alone*, but because I want to share what the early design and technology decisions were for Grace, and why I started writing it in the first place.

For shorter answers to some of these, please see [Frequently Asked Questions](Frequently%20asked%20questions.md).

---

## Table of Contents

[A word about Git](#a-word-about-git)

[User experience is everything](#user-experience-is-everything)

[Excellent perceived performance](#excellent-perceived-performance)

[CLI + Native GUI + Web UI + Web API](#cli--native-gui--web-ui--web-api)

[F# and Functional programming](#f-and-functional-programming)

[Cloud-native version control](#cloud-native-version-control)

[Why Grace is centralized](#why-grace-is-centralized)

[Performance; or, Isn't centralized version control slower?](#performance-or-isnt-centralized-version-control-slower)

[Scalability](#scalability)

[Monorepos](#monorepos)

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## A word about Git

It's not possible to design a version control system (VCS) today (Grace was first conceived in December, 2020) without designing something that relates to, interfaces with, and/or somehow *just reacts to* Git. In order to explain some of the choices I've made in Grace, I *have* to mention Git. Mostly, of course, I'll do that if I think Grace is better in some way or other.

With that said, and just to be clear... I respect Git enormously. It will take years for any new VCS to approximate the feature-set of Git. Until a new one starts to gain momentum and gets a sustained programming effort behind it - open-source and community-supported - every new VCS will sort-of be a sketch compared to everything that Git can do.

The maintainers of Git are among the best programmers in the world. The way they continue to improve Git's scalability and performance, year-after-year, while maintaining compatibility with existing repositories, is an example of how to do world-impacting programming with skill and, dare I say, grace.

Git has been around for 17 years now, and it's not disappearing anytime soon. If you love Git, if it fits your needs well, you will be able to continue to use Git for at least the next 20 years without a problem. (What source control might look like in 2042 is anyone's guess.)

Whether Git will remain the dominant version control system for that entire time is quite another question. I believe that *something else* will capture people's imagination enough to get them to switch away from Git at some point. My guess about when that will happen is: soon-ish. Like, *something else* is being created now-ish, \<waves hands\>±2 years. There are some wonderful source control projects going on right now that are exploring this space. I offer Grace in the hope that *it* will be good enough to make people switch. Time will tell.

Git is amazing at what it does. I'm openly borrowing from Git where I think it's important to (ephemeral working directory, lightweight branching, SHA-256 hashes, and so much else).

I just think that it's a different time now. The constraints that existed in 2005 in terms of hardware and networking, that Git was designed to fit in, don't hold anymore. We can take advantage of current client and server and cloud capabilities to design something really different.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## User experience is everything

Now that I've said some nice things about Git...

### Git's UX is ~~terrible~~ uh... not always great.
 
 [I](https://xkcd.com/1597/) [hope](https://gracevcsdevelopment.blob.core.windows.net/static/RandomGitCommands.jpeg) [this](https://git-man-page-generator.lokaltog.net) [is](https://rakhim.org/honestly-undefined/13/) [not](https://gracevcsdevelopment.blob.core.windows.net/static/MemorizingSixGitCommands.jpg) [a](https://www.quora.com/Why-is-Git-so-hard-to-learn) [controversial](https://www.quora.com/If-I-think-Git-is-too-hard-to-learn-does-it-mean-that-I-dont-have-the-potential-to-be-a-developer) [statement](https://twitter.com/markrussinovich/status/1395143648191279105). And [I](https://twitter.com/robertskmiles/status/1431560311086137353) **[know](https://twitter.com/markrussinovich/status/1578451245249052672)** [I'm](https://ohshitgit.com/) [not](https://twitter.com/dvd848/status/1508528441519484931) [alone](https://twitter.com/shanselman/status/1102296651081760768) [in](https://www.linuxjournal.com/content/terrible-ideas-git) [thinking](https://blog.acolyer.org/2016/10/24/whats-wrong-with-git-a-conceptual-design-analysis/) [it](https://matt-rickard.com/the-terrible-ux-of-git).

Learning Git is far too hard. It's basically a hazing ritual that we put ourselves through as an industry. Git forces the user to understand far too much about its internals just to become a proficient user. Maybe 15%-20% of users really understand it. Many of its regular users are literally afraid of it. Including me.

### Grace's UX is much, much nicer.

Grace is explicitly designed to be easy to use, and easy to understand.

It's as simple as possible. It has abstractions. Users don't have to know the details of how it works to use it.

Because of that, Grace has fewer concepts for users to understand to become and feel proficient in using it.

Grace formats output to make it as easy to read as possible, and also offers JSON output, minimal output, and silent output for each command. [^output]

And in a world where hybrid and remote work is growing, Grace offers entirely new experiences with a live, two-way channel between client and server, linking repository users together in new ways, including auto-rebasing immediately after merges.

There's so much more to do in UX for version control. Grace is a platform for exploring where it can go next, while remaining easy to use, and easy to understand.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Excellent perceived performance
Measuring actual performance of any significant system is important, and Grace will have performance benchmarks that can be run alongside other kinds of tests to ensure that regressions don't sneak in. I care deeply about performance.

What I care even more about is _perceived performance_. What I mean by that is something more subjective than just "how many milliseconds?" I mean: **does it _feel_ fast**?

Perceived performance includes not just how long something takes, but how long it takes relative to other things that a user is doing, and how consistent that time is from one command invocation to the next, to the next, to the next.

My experience is that running _fast enough_, _consistently_, is what gives the user the feeling that a system is fast and responsive.

That's what Grace is aiming for: both _fast_, and _consistent_. Fast + consistent means that users can develop expectations and muscle memory when using a command, and that running a command won't take them out of their flow.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## CLI + Native GUI + Web UI + Web API
Another avenue for providing great UX is in providing choices about how to interact with Grace.

### CLI
Obviously, Grace starts with a command-line interface (CLI). I've designed it for ease-of-use. As much as possible, default values are used to save typing. By default, most output is shown in tables with borders, and is formatted to help guide your eyes to the important content. Grace can also provide output in minimal text, JSON, verbose, or no output at all (silent).

### Native GUI
Wait, what? No one does those anymore.

Yeah, well... the thing is, I really don't like Electron apps. Like, at all. I'm in the "it's called a _browser_... it's for _browsing_..." camp.

It's simply not possible to recreate the snappiness, the stick-to-your-finger-ness, the _certainty_ that the UI is reacting to you, that you get in a native app when you're writing in a browser. It's just not. I've been watching this for years now, and almost no one even tries.

"What about Visual Studio Code?" I hear someone say. It's among the best examples, for sure. (I admit it, I'm typing this in VS Code right now.) But I don't _love_ it. Look how much programming and how many person-years have gone into it to make it OK. Look at the way they had to completely rewrite the terminal window output using Canvas because nothing else in a browser was fast enough.

I don't see a lot of other Electron apps getting nearly that level of programming. And they're all just... not great. And I fear that, as an industry, we're failing our fellow human beings, and we're failing each other, by accepting second-rate UX on the first-rate hardware we have.

We're making this choice for one reason: our own convenience as programmers. We're prioritizing developer ergonomics over user experience, and claiming that it's for business reasons. And we're usually not making great experiences, even with that.

We have tools today that can create native apps on multiple platforms from a single codebase, and that's what I'm taking advantage of. There's nothing in Grace's UX that I'm currently imagining that requires any complex user controls that can't be rendered in any of those tools... you know: text blocks, lines, borders, normal input fields and buttons / checkboxes / radio buttons. Maybe a tree view or some graphs if I'm feeling fancy.

We can provide incredible experiences when we take advantage of the hardware directly, and I intend to.

### Web UI
So, after all that... I'm creating a Web UI? What gives?

Browsers are great for browsing and light functionality, and that's all Grace will need.

### Web API
Grace Server itself is simply a modern, 2022-style Web API. If there's something you'd rather automate by calling the server directly, party on. Grace ships with a .NET SDK (because that's what the CLI + Native GUI + Web UI use), and that SDK is simply a projection of the Web API into a specific platform. It should be trivial to create similar SDK's for other languages and platforms.

It's about choices for the user. It's about understanding that sometimes the best way to share something is with a URL. And it's about providing a place that we can collaborate on what the default Grace's UI should look like.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## F# and functional programming

### Grace is written primarily in F#

One reason for this is simple: **F# is my favorite programming language right now**. It's beautiful, and it feels lightweight, but it's really strongly-typed and very fast.

But... there are other reasons.

### Reconsidering object-oriented programming

Like many of my peers, I've been specializing in object-oriented (OO) code for a long time. For me, it was C++ starting in 1998, and then .NET starting with .NET Framework Beta 2 in June, 2001. I've written tens-of-thousands of lines of object-oriented code. In 2015, I started to learn Category Theory, and in 2017-18, I had the opportunity to work on a project at Microsoft Research that was written in F#. I went back to C# for a bit after that, but, the seed was planted, and in a small 2020 pandemic side project, I decided to use F# to _really_ learn to think functionally and put a little bit of that Category Theory to use.

After 20+ years of writing OO code, I've come to the conclusion, as have others, that we've hit a ceiling in quality and maintainability in object-oriented-ish code for anything beyond a medium-sized codebase. We've adopted many practices to cover up for the problems with OO, like dependency injection and automated unit testing so we can refactor safely, but the truth is that without a significant investment in Developer Experience, and sustained effort to just keep the code clean, many large OO projects become supremely brittle and hard to maintain.

You may disagree, and that's fine. There's a fair argument that when you design OO systems as message-passing systems (and Grace does this using the Actor pattern) they factor really well. I'm not saying it's not possible to have a large and still-flexible OO codebase, just that it's rare and takes deliberate effort to keep it that way.

Functional programming offers a new path to create large-scale codebases that sidestep these problems. No doubt, over the coming years, as more teams try functional code, we'll find anti-patterns that we need to deal with (and, no doubt, I have some of them in Grace), but having personally taken the mindset journey from OO to functional, my field report is: we'll benefit greatly as an industry if we take a more functional and declarative approach to coding. It can do wonders everywhere, not just in the UI frameworks where we've already seen the benefits.

Whether you choose Haskell, Scala, F#, Crystal, or some other functional language, I'd like to invite you to try functional programming. It's a journey, for sure, but it's so worth it. Not only will you learn a new way to think about organizing code, you'll become a better OO programmer for it.

### .NET is really nice to use, and well-supported

For those who haven't worked with it yet... .NET is great now. Really. Let me explain why.

The old days of .NET being a Windows-only framework are long-since over. .NET is fully cross-platform, suporting Windows, Linux, MacOS, Android, and iOS. It's the most well-loved framework according to [Stack Overflow's 2022 Developer Survey](https://survey.stackoverflow.co/2022/#section-most-popular-technologies-other-frameworks-and-libraries), as it was in [2021](https://insights.stackoverflow.com/survey/2021#section-most-popular-technologies-other-frameworks-and-libraries), and Microsoft has continued to pour work into making it faster, better, easier-to-use, and well-documented. NuGet, .NET's package manager, has community-supported packages for almost every technology one might wish to interface with.

In terms of performance, .NET has been near the top of the [Techempower Benchmarks](https://www.techempower.com/benchmarks/#section=data-r21&test=composite) for years, and the .NET team and community continue to find performance improvements in every release.

As far as developer experience, .NET is just a really nice place to spend time. The documentation is amazing, the examples and StackOverflow support are first-rate.

Is it perfect? No, of course not. Nothing in our business is.

Does it get to "really good" and "great" more often than other frameworks / runtimes / languages? Does it continue to improve release after release? In my experience: yes, it does.

I'm not aware of a programming framework that I think has a better chance of being well-supported for at least the next 10 years than .NET.

So, it's very fast, it has great corporate and community support, it runs on every major platform, and it's loved by those who use it. It's a safe choice, it's a good choice, and I'm happy to be a .NET developer.

### Source control isn't "systems-level"
I like things that go fast. My second programming language - at age 11 - was 6502 Assembler. I've written and read code in IBM 370 Assembler and 80x86 Assembler. I've written C and C++, and continue to pay attention to the wonderful work being led by [Herb Sutter](https://www.youtube.com/user/CppCon/search?query=herb%20sutter) and [Bjarne Stroustrup](https://www.youtube.com/user/CppCon/search?query=bjarne) to make C++ faster, safer, less verbose, and easier to use. I applaud the work by Mozilla and the Rust community to explore the space of safer, very fast systems programming. I consider any public talk by [Chandler Carruth](https://www.youtube.com/results?search_query=chandler+carruth) to be mandatory viewing.

I'm aware of what it means to be coding down-to-the-metal. I grew up on it, and still try to think in terms of hardware capabilities, even as I use higher-level frameworks like .NET.

With that said, the idea that version control systems have to be written in a systems-level language, just because they all used to be, isn't true, especially for a centralized VCS that's really just a modern Web API + clients. Grace relies on external databases and object storage services, and so there's very little Git-style byte-level file manipulation going on, and where there is, .NET can tell the file system to do stuff just as quickly as any other framework. Given how fast .NET is (within 1% of native C++ when well-written), and the fact that network round-trips are involved in most things that Grace does, it's just not likely that writing Grace in C++ or Rust would make a difference in perceived performance for users. Most of the time is spent waiting for something over the network, both in the client, and on the server. The computation part is usually pretty quick compared to that.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Cloud-native version control

I've personally installed and maintained hundreds of servers and virtual machines in my career. I racked some of them myself. It seemed fun at the time. I'm over it.

That's why I'm a huge fan of Platform-as-a-Service (PaaS), and why Grace was imagined on its first day as a cloud-native system. I starting tracking the [Dapr project](https://dapr.io) as soon as it was announced, and saw it as a perfect solution for being able to write a cloud-native, PaaS-based system, while allowing everyone to choose their own deployment adventure.

### Your choice of services at deployment time

Grace runs on Dapr to allow you to choose which PaaS or cloud or even on-premises services it runs on.

The database, the observability platform, the service bus / event hub pieces, and more, will be configurable by you. Grace will run on anything Dapr supports.

### Object storage is different

Grace uses an object storage service to save each version of the files in a repo (i.e. Azure Blob Storage, AWS S3, Google Cloud Storage, etc.). Although Dapr does support pluggable object storage providers, using Dapr for Grace's object storage isn't appropriate for Dapr's design.

Dapr is perfect for using object storage for storing smaller blobs, and although most code files fall in the size range that works well for Grace, I want Grace to support virtually unlimited file sizes. That means that it's best for Grace to directly use the specific API's for the storage providers, and to allow the CLI / client to communicate directly with the object storage service, offloading that work to the service where it belongs.

#### A note about the actual current state of Grace

Thus far, Grace has been written only to run on Microsoft Azure. (It's the cloud provider I know best.)

There was an issue with Dapr when I started writing Grace that caused me to "work around" Dapr's support for databases. It has since been fixed - the ability to query actor storage using a Dapr-specific syntax - and I intend to remove the Azure Cosmos DB code I wrote in favor of that Dapr code over the coming months, enabling Grace to run not just on Cosmos DB, but on any data service that Dapr supports for actor storage.

As mentioned above, the best thing for Grace is to directly use the specific API's of the object storage providers in the client. To do that securely, at a minimum, the object storage provider must support the concept of a time-limited and scope-limited token that can be generated at the server to be handed to the client for directly accessing the object storage service. (For example, Azure Blob Storage has [Secure Access Signatures](https://docs.microsoft.com/en-us/azure/storage/common/storage-sas-overview).)

Although I've only implemented support for Azure Blob Storage so far, I've created some light structure in the code using discriminated unions to try to keep me honest and able to implement support for other object storage services without too much difficulty.

### How does Dapr affect performance?

The simple version is: it adds 1-2ms per request through Dapr, when we ask Dapr's Actor Placement Service (running in a separate container) which Grace application instance will have the specific Actor we're interested in. It's negligable compared to overall network round-trip between client and server, and well worth it for the ease-of-use of the Actor pattern in Dapr.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Why Grace is centralized

Grace is a centralized version control system (CVCS). To be clear, there are valid reasons to use a distibuted version control system (DVCS), like Git. There are other new DVCS projects underway, and there are some great ideas in them. Grace is clearly not well-suited for a situation where a DVCS is required, and that's OK.

I wanted to take a different approach with Grace, because:

- by removing the complexity of being distributed, Grace's command surface can be much simpler to use and understand
- as long as Grace is *fast enough* (see [below](#performance-or-isnt-centralized-version-control-slower)), and easy to use, most users won't care if it's centralized
- being centralized allows Grace to handle arbitrarily large files, and to give users controls for which files get retrieved locally
- being centralized allows Grace to scale up well by relying on mature Platform-as-a-Service components
- it's 2022, and writing software that requires a file server seems... dated
- I'm not smart enough to write a better DVCS protocol and storage layer than Git
- the "I have to be able to work disconnected" scenario is less-and-less important
  -  a growing number of developers today use cloud systems as part of their development and production environments, and if they're not connected to the internet, having their source control unavailable is the least of their problems
  - in the coming years, satellite Internet will provide always-on, high-speed connections in parts of the world that were previously cut-off or limited

And, let's be honest: almost *everyone* uses Git in a pseudo-centralized, hub-and-spoke model, where the "real" version of the repo - the hub - is centralized at GitHub / GitLab / Atlassian / some other server, and the spokes are all of the users of the repo. In other words, we're already using Git as a centralized version control system, we're just kind-of pretending that we're not, and we're making things more complicated for ourselves because of it.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Performance; or, Isn't centralized version control slower?

I've been around long enough to have used a couple of the older CVCS's, and I understand the reputation of them as feeling, just... slower. And heavier. That's not what Grace is like.

Grace is designed to feel lightweight, and to be *consistently fast*, by which I mean: 

1. running a command in Grace should never take so long that it takes you out of your flow
2. running the same Grace command (i.e. `grace commit` or `grace diff` or whatever) should take roughly the same amount of time *every time*, within a few hundred milliseconds (i.e. within most users' tolerance for what they would call "consistent").

### Git is faster than Grace...

Git is really fast locally, and because almost every command in Grace will require at least one round-trip to the server, there are some commands for which Grace will never be as fast as Git. In those situations, my aim is for Grace to be as-fast-as-possible, and always *fast enough* to feel responsive to the user. I expect most Grace commands to execute in under 1.0s on the server (+network round-trip, of course); so... slower than local Git, but *fast enough*.

### ...except when Grace is faster than Git

There are also scenarios where Grace will be faster than Git - scenarios where Git communicates over a network - because, in Grace, the "heavy lifting" of tracking changes and uploading new versions and downloading new versions will have been done already, in the background (with `grace watch`). In those use cases, like `grace checkpoint` and `grace commit`, the command is just creating new database records, and that's easily faster than `git push`.

So, Grace is designed to be *fast*, i.e. fast enough to keep users in flow, and to be *consistent*, i.e. users quickly develop muscle-memory for how long things take, helping them stay in flow. CVCS's just have a different performance profile than DVCS's, but there's no reason they can't *feel* responsive and fast.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Scalability

Grace has no binary data format of its own, and relies on object storage (i.e. Azure Blob Storage, AWS S3, etc.) to store individual versions of files. Likewise, Grace's metadata is stored as documents in a database (i.e. Azure Cosmos DB, MongoDB, Redis, etc.) and not in a file on a filesystem. Therefore, to a large extent, the scalability of an installation of Grace depends on the Platform-as-a-Service components that it is deployed on.

Because Grace uses the Actor pattern extensively, Grace benefits when more memory is available for each server container instance, as Grace will automatically use that memory as a cache, reducing pressure on the underlying database. And because Grace Server is stateless, and Dapr's Actor Placement service automatically rebalances whenever an application instance is added or removed, Grace can scale up and scale down automatically as traffic increases or decreases, using standard [KEDA](https://keda.sh/) counters to drive those actions.

I haven't yet run load tests, but... if the database used for Grace can support thousands of transactions/second, and the object storage service can handle thousands of transactions/second (and the message bus and the observability system etc.), then between that and Grace's natural use of memory for caching, Grace Server *should* be able to scale up and scale out pretty well. (I hope to do some first-ever load tests in Sept/Oct 2022 and no doubt I'll find some performance fixes when I do.)

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
## Monorepos

Defining what a "large repository" or a "monorepo" is isn't straightforward. "Large" can mean different things:

- a large number of files
- a large number of versions of files
- a large number of updates-per-second
- a large number of users/services hitting the repo at the same time
- large binary files versioned in the repo
- some or all of the above, all at the same time.

Grace is designed to handle all of these scenarios well. Grace decomposes a repository from being "one big ball of bytes" into being individual files in object storage, and individual documents in a database representing the state of repositories, branches, directories, and everything else. This way of organizing the data about the repository allows commands and queries to run just as fast for monorepos as they do for small and medium-sized repos. 

There are, of course, some operations that will take longer on larger repositories (`grace init` is an obvious example where a lot of files might need to be downloaded), but, in general, Grace Server's performance shouldn't degrade as the repository size grows. (Grace CLI as well... *if* you're running `grace watch`).

[^output]: That's the intention, anyway. I have some work to do on some of the commands to light all of that up.