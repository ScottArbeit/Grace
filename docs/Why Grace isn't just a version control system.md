# Why Grace isn't just a version control system

## New circumstances require new tools

When I first created Grace, before AI coding was a thing, I worked for GitHub, and I designed it as a pure version control system. A frame I had in mind for it as I designed it was "What belongs to Git, and what belongs to GitHub?"

GitHub already had Pull Requests, and Issues, and all of the other services and products that wrap around Git, and I didn't want to step on that. If my goal was to write something that could sit next to Git, at scale, for GitHub (and GitLab, etc.) then, I thought, it made sense to keep Grace fitting more-or-less in the same architectural box that Git sits in for large hosters.

Well, if you're reading this, you're aware of how much software development has changed, and is changing, thanks to agentic coding.

When work methods change this drastically, we have to reconsider what the right kinds of tools are, and whether the old tools are shaped correctly to meet the new requirements. And, sorry, but Git's not good enough anymore. Not even close.

Steve Yegge's [Beads](https://github.com/steveyegge/beads) project, which I've used a bit for Grace, made me realize that capturing intention, and being able to track separate, small work items as part of building a feature, _alongside_ the actual code changes, was too important to leave as just-one-more-thing that we shove into Git as a file. Beads is a cute, not-exactly-designed vibe-coded hack in this direction (and I say that with love) but I think we need to have something like that as a more-deliberately-designed part of our version control now.

If you've looked into context engineering, you know that it's better for the agents to have separate, small work items. For humans reviewing that work, having everything in one place, linked together - the intention, the work item, the automated reviews, the task summaries, the diffs, all of it, linked to the exact version of the code we're interested in - gives us all of the context we need to understand what happened, and why it happened.

The fact that I'm seeing work tracking like this get built by multiple teams in multiple ways in multiple products says that it's a great candidate for including directly with the code, where all of the agents and all of the humans can find it and use it easily.

And then I thought about how AI should fit into the creation, review, and promotion of code. Grace's event system gives us an unprecedented ability to respond to code changes in (near) real-time. What kinds of both inexpensive, determininistic code review, and costlier but more sophisticated AI reivew, should be built-in to Grace? How could we plug AI in at that level, in a way that helps resolve conflicts and enable maximum velocity? I'm still exploring that, and I'm sure I don't have all of the answers, but I know that these are crucial new kinds of primitives that deserve to be first-class constructs in a version control system now.

Grace is about helping you stay calm, stay in control, and stay in flow. The more I've worked on it, the more I can't imagine not having work tracking and automated review and everything else I'm including now sitting alongside the exact code versions.

I'm sure the surface area of these parts of Grace will change as we get feedback and iterate. I don't claim to have it exactly right.

I do know that the minimum bar for having a useful version control system in the late 2020's has gone way, way up. Just storing the code (and only if the files are small) isn't enough anymore.
