namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Diff =
    
    type DiffParameters() = 
        inherit CommonParameters()
        member val public DirectoryId1 = DirectoryId.Empty with get, set
        member val public DirectoryId2 = DirectoryId.Empty with get, set

    type PopulateParameters() =
        inherit DiffParameters()

    type GetDiffParameters() =
        inherit DiffParameters()

    type GetDiffBySha256HashParameters() =
        inherit DiffParameters()
        member val public Sha256Hash1 = Sha256Hash String.Empty with get, set
        member val public Sha256Hash2 = Sha256Hash String.Empty with get, set
