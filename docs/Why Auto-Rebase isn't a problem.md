# Why Auto-Rebase isn't a problem

One of the more common feedback items I've gotten about Grace is around the idea of auto-rebasing - both positive and negative. While I agree that having auto-rebase as the default is one of the more... _progressive_ ideas in Grace, I've been surprised at some of the reaction to it.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## This has never been a thing before. Why does Grace need it?

Need is a strong word... but there are two big reasons for having auto-rebase as the default behavior in Grace.

First, the design of [single-step branching](.\Branching%20Strategy.md) means that a user can't promote to `main` without being rebased on the most recent promotion in `main`. Given that over 95% of rebases are a non-event, and serve only to increase quality by making sure that the code that's getting into `main` is always tested in its latest state, it's an easy choice, and will enable users to be ready to promote at all times.

When promotion conflicts do happen, Grace's design point-of-view is that they should be dealt with immediately, and that they shouldn't wait until a PR is ready, when you get the bad news right after you think your work is done. By handling conflicts up-front, you ensure that your dev and test efforts are going to the _full, actual_ code that will be in production, and not just on the changes that you're making, separate from what the rest of your team is doing.

So, it's a branching design decision, it's a quality decision, and it's a huge part of minimizing promotion conflicts.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## What if my changes get overwritten?
In Grace, they won't.

The only way an auto-rebase can happen is if `grace watch` is running, and if `grace watch` is running, then the complete state of your branch was uploaded to the server the last time you saved a file, so nothing can be lost. If you need to look at the state of your branch from before the rebase, it'll be available, and you can always run `grace diff` to see what changed.

If you're editing the same file that got updated in the promotion that you're about to be auto-rebased on, you'll be alerted for a potential promotion conflict, and given the choice of how to handle it.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## What if I'm in the middle of debugging something?
The concern here is that you'll be in a debugging session - which, in my long but admittedly anecdotal experience, usually lasts from seconds to a few minutes - and that a promotion to `main` will happen exactly at that time, and therefore an auto-rebase will occur and one of your source code files will be updated in your working directory.

By "debugging session" what I mean is: debugging starts when you click on `Start debug` in your IDE, or run some command-line that starts a debugging session that you can act on, and it ends when you click `Stop Debug` or end the process you started at the command-line. Or \<waves hands\> something like that.

First, is it statistically likely that you'll be debugging right when `main` gets updated? For me, in the repos that I've worked in, it's not likely, but that might not be true for you.

If you happen to run extra-long debugging sessions, and auto-rebase really would be disruptive on your machine, or on your branch, you can just stop `grace watch` on the your machine to make sure auto-rebase can't happen. Alternatively, there will be ways to [turn it off](#can-you-turn-it-off).

The IDE's that I use have an option for how to handle it when files get updated by other processes, so the first thing I think is: how does your IDE handle it? Grace is not the only process that might update a file that's open in a tab in my IDE, and I know I choose the settings in my IDE's that handle that the way that works for me.

If the file getting updated is one that you're editing, and therefore debugging, that's a promotion conflict, and you'll be notified and asked how you want to handle it.

If you're in a compiled language, it won't matter because you're debugging a compiled executable. (If you're in an interpreted language, yes, there's the possibility of some amusement.)

And, last, let's just say that none of this helps, and, in fact, your debugging session does get messed up because of auto-rebase. Let's say that, because of your combination of languages and tools, you'll have to end debugging and restart debugging. And let's say that, for whatever reason, that happens to you often enough to be a problem. Well, you can always [turn it off](#can-you-turn-it-off). While I'm sure there are environments where it really would be disruptive, my experience and design perspective is that that will be rare.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Can you turn it off?

Yes, of course. (I'm not insane.) Grace will have flags at the branch level and at the repository level to turn it off, and also in the local `graceconfig.json` if you personally don't ever want auto-rebase for whatever reason.

Please give it a chance before you do, though. You might like it more than you expect.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## This is stupid, and you're stupid, and nobody wants this.

Paraphrasing some actual feedback I've gotten... well, 🤷‍♂️ I believe this is right design, and time will tell.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
