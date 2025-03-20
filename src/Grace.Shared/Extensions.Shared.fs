namespace Grace.Shared

open System
open System.Collections.Generic
open System.Linq

module Extensions =

    type Dictionary<'T, 'U> with
        /// Adds a range of key-value pairs to the dictionary.
        member this.AddRange (items: seq<KeyValuePair<'T, 'U>>) =
            items |> Seq.iter (fun kvp -> this.Add(kvp.Key, kvp.Value))
