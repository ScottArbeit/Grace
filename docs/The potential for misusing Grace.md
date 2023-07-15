# The potential for misusing Grace

Something I've thought about since the beginning of designing Grace is: how do we prevent ignorant and/or malicious management from using eventing data from Grace in the wrong way?

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Using Grace events for monitoring is fucking stupid

I'm aware of the potential to use things like "how often did this person do a save / checkpoint / commit?" as a metric, or as a proxy for productivity, or effort.

And, because I've been a programmer for several decades, I, like you, am also aware of how fucking stupid and shortsighted it would be to do so.

It's not my intention for Grace to become a monitoring platform. And there are features that Grace is meant to enable that simply can't happen without a normal, modern, cloud-native event-driven architecture.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## Even Git has timestamps

Sure, Git is distributed, and there's no way for the server to reach into your local repo and get status. If you use the feature-branch-gets-deleted method of using Git, then you're still pushing your feature branch and its commits up to the server before the merge-and-delete, and so you have that opportunity in Git to get some similar information that we're worried about in Grace. We haven't, as an industry, weaponized this data yet, so I'm hopeful we won't with any future version control system.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## So how do we prevent misuse?

If I turn off all of the events, it ruins a lot of the value proposition of Grace.

But there are approaches to it that I can imagine.

One idea is simply to not send events to the main Grace event stream for saves. The most obvious misuse is looking at saves, so maybe we just keep them as a thing for individual users, but we don't ship them over the service bus.

Another idea is just to turn off auto-saves for a repo entirely. Again, I hate losing this functionality, but it's a possibility.

There are other configurations of features I could imagine, but, ultimately, the design of it will have to come from the community, and I remain open to figuring out the right way to do it that enables the most flexibility with the most safety from idiot management.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)

## My promise: I'll be very vocal about it

In case it's not obvious from what I just said, I'm completely opposed to using events in Grace for any sort of monitoring of programmers. It's statistically stupid, it's harmful to even think there's any value in it.

And I'll be vocal about it in every public presentation, in every talk with a potential user, and I'll get other leaders around Grace to say the same thing.

I will do everything I can to make the point over and over and over, from every angle that I can.

I know that's not an iron-clad guarantee that idiots won't idiot, but I'll sure as hell call them out for doing it, and demand that they stop.

![](https://gracevcsdevelopment.blob.core.windows.net/static/Orange3.svg)
