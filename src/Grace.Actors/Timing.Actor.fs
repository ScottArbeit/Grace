namespace Grace.Actors

open Grace.Actors.Context
open Grace.Actors.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Linq
open System.Reflection
open System.Diagnostics

module Timing =

    let publishTimings sb =
        let message = sb.ToString()
        logToConsole message

    let addTiming flag actorStateName correlationId =
        let timingList = timings.GetOrAdd(correlationId, (fun _ -> List<Timing>()))
        let timing = Timing.Create flag actorStateName
        timingList.Add(timing)
        ()

    let reportTimings path correlationId =
        let mutable timingQueue = null
        let sb = stringBuilderPool.Get()

        try
            if timings.TryGetValue(correlationId, &timingQueue) then
                match timingQueue.Count with
                | 0
                | 1 -> ()
                | _ ->
                    let timingList = timingQueue.ToList()

                    sb.AppendLine().AppendLine(String.replicate 80 "=") |> ignore

                    sb.AppendLine($"CorrelationId: {correlationId}; Path: {path}; Timings: {timingList.Count} ")
                    |> ignore

                    sb.AppendLine(String.replicate 80 "-") |> ignore

                    sb.AppendLine($"  {formatInstantExtended timingList[0].Time}: {getDiscriminatedUnionCaseName timingList[0].Flag}")
                    |> ignore

                    for i in 1 .. timingList.Count - 1 do
                        let previousTiming = timingList[i - 1]
                        let currentTiming = timingList[i]

                        let previousActorStateName =
                            if String.IsNullOrEmpty(previousTiming.ActorStateName) then
                                String.Empty
                            else
                                ":" + previousTiming.ActorStateName

                        let currentActorStateName =
                            if String.IsNullOrEmpty(currentTiming.ActorStateName) then
                                String.Empty
                            else
                                ":" + currentTiming.ActorStateName

                        let milliseconds = $"{(timingList[i].Time - previousTiming.Time).TotalMilliseconds:F3}"
                        let paddedDuration = (String.replicate (Math.Max(7 - milliseconds.Length, 0)) " ") + milliseconds // Right-align, 7 characters.

                        sb.AppendLine(
                            $"  {formatInstantExtended currentTiming.Time}: Duration: {paddedDuration}ms; {getDiscriminatedUnionCaseName previousTiming.Flag}{previousActorStateName} -> {getDiscriminatedUnionCaseName currentTiming.Flag}{currentActorStateName}"
                        )
                        |> ignore

                    let duration = timingList.Last().Time.Minus(timingList.First().Time)
                    let milliseconds = $"{duration.TotalMilliseconds:F3}"
                    let paddedDuration = (String.replicate (Math.Max(7 - milliseconds.Length, 0)) " ") + milliseconds // Right-align, 7 characters.

                    let space = " "

                    sb.AppendLine(String.replicate 80 "-") |> ignore

                    if duration.TotalMilliseconds > 500.0 then
                        sb.AppendLine($"{String.replicate 32 space}Total:    {paddedDuration}ms ##########")
                    else
                        sb.AppendLine($"{String.replicate 32 space}Total:    {paddedDuration}ms")
                    |> ignore

                    sb.AppendLine(String.replicate 80 "=") |> ignore

                // Write the timings.
                if sb.Length > 0 then publishTimings sb
        finally
            stringBuilderPool.Return(sb)

    let removeTiming correlationId =
        let mutable x = null
        timings.TryRemove(correlationId, &x) |> ignore
