# Grace Code Review instructions

This file is intended to be the guidance for GitHub Copilot to perform automated code reviews in Grace PR's.

## Code Review instructions

_note: for now, these instructions are in no particular order, I'm just capturing ideas._

### F# Standards

Rules in this section deal with our use of F# in the codebase, and specify stylistic and syntactical choices we mnight make.

#### Always use `task { }`; never use `async { }` for asynchronous code.

Older versions of F# used the `async { }` computation expression to write asynchonous code. In fact, the C# `async/await` syntax, which has since been adopted by TypeScript and JavaScript, was inspired by `async { }` in F#.

The `task { }` computation expression for asynchonous code was added in F# 6.0. `task { }` uses the same stuff from `System.Threading.Tasks` that C# does for `async/await` code, allowing it to take advantage of all of the performance improvements in Tasks that each version of .NET delivers.

Any use of `async { }` in Grace should be considered an error and should be rewritten using `task { }`.

### Internal consistency

Rules in this section are intended to enforce consistent use of utilities and constructs provided by Grace.

#### All grain references must have the CorrelationId set using RequestContext.Set().

The actors expect to be able to call `RequestContext.Get(Constants.CorrelationId)` to get the CorrelationId when they need it, and so the grain client is responsible for setting the CorrelationId on it by calling `RequestContext.Set(Constants.CorrelationId, <some correlationId>)`.

The easiest way to do this is to use the ActorProxy extensions in ActorProxy.Extensions.Actor.fs. Each of those helper methods - `Branch.CreateActorProxy`, `Repository.CreateActorProxy`, etc. - takes `correlationId` as a parameter. They each set the CorrelationId for you in the RequestContext.

If any grain references are created by calling `IGrainFactory.GetGrain<'TGrainInterface>(key)` without using the ActorProxy extensions, they should be closely inspected to ensure that `RequestContext.Set(Constants.CorrelationId, <some correlationId>)` is called before any use of the grain reference to call a grain interface method. The recommendation is to rewrite the code to use the ActorProxy extensions to eliminate any possibility of an error.
