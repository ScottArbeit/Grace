# Grace - Version Control for the AI Era

<!-- markdownlint-disable MD013 -->

grace _(n)_ -

1. elegance and beauty of movement, form, or expression
2. a pleasing or charming quality
3. goodwill or favor
4. a sense of propriety and consideration for others [^grace]

![](./Assets/Orange3.svg)

Grace is a **version control system** designed and built for the AI Era.

Grace is designed for kindness to humans, and velocity for agents. It's meant to help you stay calm, in-control, and in-flow as you ship code at agentic speed.

It has all of the basics you'd expect from a version control system, plus primitives that help you monitor your agents, capture the work they're doing, and review that work in real-time using both deterministic checks and AI prompts that you can write, with rules that you set.

Grace assumes that AI belongs in the version control system, reacting to events, reviewing changes, and generating whatever you need to get code from idea to agent to production.

Along with version control, Grace is designed to capture the work items, the prompts, the specifications, the model versions, and so much more - everything that goes into doing agentic coding - so you and your agents have the best possible context to complete the work successfully, and review it with confidence.

All you have to do is tell your agents to run `grace agent bootstrap` at the start of the session. Grace and your agents will take care of the rest, automatically capturing what's going on. The `bootstrap` is customizable, and Grace will even help you customize it.

In the repository, you define the checks you want run in respose to which events, you define the prompts, you choose the models, and Grace Server will do the rest, capturing the results in detailed Review reports. Again, Grace will help you define and customize all of it.

Of course it's fast. Grace is multitenant, built for massive repositories, insane numbers of developers and agents, and ludicrous amounts of code and binary files of any size.

It ships with a promotion queue - Grace doesn't do merges, it does promotions - and automatically handles promotion conflict resolution according to rules and confidence levels you set.

> ⚠️👷🏻🚧 Grace is an alpha, and is going through rapid evolution with breaking changes, but it's ready for feedback and contributions. It is not ready for or intended for production usage at this time.

![](./Assets/Orange3.svg)

## Technology stack

Grace is a modern, fast, powerful centralized version control system. It's made up of a web API, with a CLI (and soon a GUI).

Grace is written primarily in **F#**, and uses:

- **ASP.NET Core** for the HTTP API
- **Orleans** for the virtual-actor and distributed-systems core
- **Microsoft Azure PaaS services**[^1]:
  - **Azure Cosmos DB** for actor state storage (repos, branches, references, directory versions, etc.) at ludicrous scale and speed
  - **Azure Blob Storage** for objects and artifacts, including (virtually) unlimited-size binary files
  - **Azure Service Bus** for event streams that you can hook into
  - All come with emulators for frictionless local development
- **SignalR** for live client-server coordination (`grace watch`)
- **Redis** (used by SignalR and for caching)
- **Aspire** to orchestrate everything for both local dev and cloud deployment
- **Avalonia** (I think) for a fully cross-platform GUI, including WASM

[^1]: Grace is designed to be adaptable to AWS and other cloud providers, and with coding agents, it should be not easy but not too hard to do. I just haven't done it yet.

## Running Grace locally

The fastest way to understand Grace is to run it locally and poke at it.

Grace is designed as a multitenant, massively scalable, centralized, cloud-native version control system, so running it locally isn't quite as simple as "download this one executable and run it". Grace uses [Aspire](https://aspire.dev) to make running Grace as simple as possible, with configurations for using either local emulators or actual Azure services.

### Normal development and debugging

In normal development and debugging, there are three steps to running Grace.

1. Run the Aspire AppHost project. The AppHost will start the local emulators (if requested), and run Grace Server.
2. Use the Grace CLI to do Grace stuff.
3. Stop the Aspire AppHost. The AppHost will stop the local emulators and Grace Server as it exits.

### First time setup

The first-time steps below use **local emulators** and **test authentication** (i.e. the same authentication we use in integration tests), so you don't have to set anything up in the cloud to get started. If all goes well, you should be up and running in under 10 minutes.

> There is a detailed guide to configuring authentication at [`/docs/Authentication.md`](/docs/Authentication.md).

1. **Install prerequisites** for your platform:
   - [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download)
   - [PowerShell 7+](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell)
   - A container runtime
     - Aspire supports [Docker Desktop](https://www.docker.com/get-started/) and [Podman](https://podman-desktop.io/).
     - [Rancher](https://www.rancher.com/products/rancher-desktop) is not officially supported, but with Moby/dockerd it should work.

    NOTE: If using Podman, set the [container runtime](https://aspire.dev/app-host/configuration/#common-configuration) to `podman`:

    PowerShell:
    ```powershell
    $env:ASPIRE_CONTAINER_RUNTIME="podman"
    ```

    bash/zsh:
    ```bash
    export ASPIRE_CONTAINER_RUNTIME=podman
    ```

2. **Clone the Grace repo**: `git clone https://github.com/ScottArbeit/Grace.git` or `gh repo clone ScottArbeit/Grace`.
3. **Build the solution**: `dotnet build ./src/Grace.slnx` to sanity-check your environment.
4. **Create an alias to make your life easier**: Add an alias to your profile called `grace` that points to `./src/Grace.CLI/bin/Debug/net10.0/grace.exe`.

   PowerShell:

   ```powershell
   Set-Alias -Name grace -Value \<repo-path\>\src\Grace.CLI\bin\Debug\net10.0\grace.exe
   ```

   bash / zsh:

   ```bash
   alias grace="\<repo-path\>/src/Grace.CLI/bin/Debug/net10.0/grace.exe"
   ```

5. **Choose a test repository**: You can create an empty directory to start with a blank repo in, or you can copy or clone some code into a directory to start with that code.

6. **Start Grace Server**: Run `pwsh ./scripts/dev-local.ps1` to start Grace Server using Aspire. This will automatically generate a personal access token that you'll use for authentication.

    When `dev-local.ps1` finishes, it will output your new token, along with exact copy/paste commands to set `GRACE_SERVER_URI` and `GRACE_TOKEN`, the first environment variables you'll need.

7. **Create Owner, Organization, and Repository**: Copy and paste (or modify if you want) the scripts below to set up your first Grace repo.

    ```powershell
    # Create an owner, organization, and repo
    grace owner create --owner-name demo
    grace organization create --owner-name demo --organization-name sandbox
    grace repository create --owner-name demo --organization-name sandbox --repository-name hello

    # Connect to it (writes local Grace config for this working directory)
    grace connect demo/sandbox/hello

    # Initialize the repo with the contents of the current directory
    grace repository init --directory .
    ```

    To see your current state:

    ```powershell
    grace status
    ```

    To see what's changed in your branch vs. previous states, use `grace diff`:

    ```powershell
    grace diff commit
    grace diff promotion
    grace diff checkpoint
    ```

### Verify the CLI can talk to the server

Once you have `GRACE_SERVER_URI` and `GRACE_TOKEN` set, run:

```powershell
grace auth whoami
```

### File an issue if anything seems confusing or rough

The intention is for this first-time setup to be as easy as possible. If you run into any problems, please file an issue so we can make it smoother.

### Running Grace and Git in the same directory

You can use Git and Grace side-by-side in the same directory. You just have to make sure they ignore each other's object directories, `.git` and `.grace`.

#### Tell Git to ignore Grace

`.grace` is Grace's version of the `.git` directory.

To ignore it, add the path `.grace/` to your `.gitignore`.

#### Tell Grace to ignore Git

Grace's `.graceignore` file, by default, ignores the `.git` directory.

If it happens to be missing, add the path `/.git/*` to `.graceignore` in the root of your repository.

> Again, ⚠️👷🏻🚧 Grace is an alpha, and still has some alpha-like bugs. For now, I recommend testing Grace either on 1) repos that you won't be sad if something bad happens, or; 2) repos where you're comfortable running `git reset` to restore to a known-good version if you need it.

![](./Assets/Orange3.svg)

## Architecture

### 1) Grace Server is a modern web API that uses an actor system

Grace is built on **Orleans**. Most domain behavior is implemented as virtual actors, which makes it effortless and natural to scale up and scale out.

- HTTP API: `src/Grace.Server`
- Actor implementations: `src/Grace.Actors`
- Domain types and events: `src/Grace.Types`
- Shared utilities and DTOs: `src/Grace.Shared`

### 2) Grace is event-sourced

A version control system is \<waves hands\> just a series of modifications to files and branches and repositories over time. Grace stores every modification to every entity as an event, as the source of truth. Grace then uses those events to pre-compute and cache projections that help you and your agents go faster.

### 3) Files are stored in object storage

Grace relies on cloud object storage systems to provide a safe and infinitely scalable storage layer. Grace currently uses Azure Blob Storage, with the intention of adding the ability to run it on AWS S3 and others. Currently, all files are stored as single blobs in Azure; soon Grace will shift to a content-addressible storage construct that will enable efficient handling of changes to large binary files.

### 4) `grace watch` for effortless, background update tracking

`grace watch` scans the working directory when it starts to get current state and notice any changes that happened while it wasn't running, and then continuously watches for changes, saving new file and directory versions to Grace Server, all generally within a second of the file being saved. This background processing saves time in every session, as other Grace commands like `grace commit` detect `grace watch` and can skip the costly directory rescans and other work that `grace watch` has already taken care of.

Grace has other commands that agents are meant to use to capture the complete context of the work being done.

Together, they give us a step-by-step audit trail of everything an agent has done and is doing, and why, that you can watch and review in real-time from your computer.

> If you want a deeper dive on what `grace watch` does and does not do, see: [What `grace watch` does](./docs/What%20grace%20watch%20does.md).

### 5) “Continuous review” for rapid validation of changes

Grace’s review system is designed to bring AI evaluations and human review and approvals together in one harmonious flow.

Key concepts include:

- **Policy snapshots** (immutable rule bundles)
- **Stage 0 analysis** (deterministic signals recorded for references)
- **Promotion queues** and **integration candidates**
- **Gates** and **attestations**
- **Review packets** with easy-to-understand, customizable summaries

For more, see: `docs/Continuous review.md`

![](./Assets/Orange3.svg)

## Roadmap

Grace is evolving quickly. Strap in....

### GUI

- Native GUI for Windows, MacOS, Linux Desktop, Android, iOS, and WASM.
- Not Electron.

### Full rewrite of object storage layer

- Switch to content-addressable storage for more efficient object storage and network transfer
- Automatic chunking of large binary files

### Multi-hash semantics

- Add abstractions to support multiple hashing algorithms at once
- Ensure that Grace is not locked into just one hashing algorithm
- Add BLAKE3 hashing for better performance and to support content-addressable storage

### `grace cache` for CI/CD and in-office scenarios

- Built-in feature of Grace CLI
- Like a Git mirror, plus full authentication and authorization
- Pre-fetch by listening to repository events using SignalR and downloading the branches and versions you want
- Transparent read-through cache for CI workers and in-office users to save time and bandwidth

### Improved database backpressure handling

### Agent skills to help you create and update Grace's automatic review policies

### `grace agent bootstrap` command to give coding agents context for using Grace

### Ergonomics

- smoother onboarding
- better defaults

### Repeatable performance benchmarking

### Even more unit tests

### Even more integration tests

![](./Assets/Orange3.svg)

## Grace at NDC Oslo 2023

I gave my first conference talk about Grace at NDC Oslo 2023. Grace has changed _a lot_ since then, but this was the original idea. You can watch it [here](https://youtu.be/lW0gxMbyLEM):

[<img src="https://github.com/ScottArbeit/Grace/assets/2406993/2f20bf3a-9907-42d3-8596-84a7e1334f55">](https://youtu.be/lW0gxMbyLEM)

![](./Assets/Orange3.svg)

## Contributing

If you want to help shape Grace:

- Read `CONTRIBUTING.md`
- AI submissions are expected and welcomed, but they have to follow
- Open issues for rough edges, missing docs, or confusing workflows


[^grace]: Definition excerpted from https://www.thefreedictionary.com/grace.
