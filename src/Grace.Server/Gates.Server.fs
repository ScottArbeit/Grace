namespace Grace.Server

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Queue
open Grace.Types.Types
open NodaTime
open System
open System.Threading.Tasks

module Gates =
    type GateContext = { Candidate: IntegrationCandidate; PolicySnapshot: PolicySnapshot option; CorrelationId: CorrelationId; Principal: UserId }

    type IGate =
        abstract member Definition: GateDefinition
        abstract member Run: GateContext -> Task<GateAttestation>

    let private buildAttestation (gate: GateDefinition) (context: GateContext) (result: GateResult) (summary: string) =
        {
            GateAttestationId = Guid.NewGuid()
            OwnerId = context.Candidate.OwnerId
            OrganizationId = context.Candidate.OrganizationId
            RepositoryId = context.Candidate.RepositoryId
            CandidateId = context.Candidate.CandidateId
            PolicySnapshotId = context.Candidate.PolicySnapshotId
            BaselineHeadReferenceId = context.Candidate.BaselineHeadReferenceId
            GateName = gate.Name
            GateVersion = gate.Version
            Result = result
            ArtifactUris = []
            Summary = summary
            Timestamp = getCurrentInstant ()
            Principal = context.Principal
        }

    type PolicyGate() =
        let definition = { Name = "policy"; Version = "v1"; ExecutionMode = GateExecutionMode.Synchronous }

        interface IGate with
            member _.Definition = definition

            member _.Run context =
                let result =
                    match context.PolicySnapshot with
                    | Some _ -> GateResult.Pass
                    | None -> GateResult.Block

                let summary =
                    match context.PolicySnapshot with
                    | Some _ -> "Policy snapshot present."
                    | None -> "Policy snapshot missing."

                Task.FromResult(buildAttestation definition context result summary)

    type ReviewGate() =
        let definition = { Name = "review"; Version = "v1"; ExecutionMode = GateExecutionMode.Synchronous }

        interface IGate with
            member _.Definition = definition

            member _.Run context = Task.FromResult(buildAttestation definition context GateResult.Pass "Review gate stub.")

    type BuildTestGate() =
        let definition = { Name = "build-test"; Version = "v1"; ExecutionMode = GateExecutionMode.AsyncCallback }

        interface IGate with
            member _.Definition = definition

            member _.Run context = Task.FromResult(buildAttestation definition context GateResult.Skipped "Build/test gate stub.")

    let private gates: IGate list =
        [
            PolicyGate() :> IGate
            ReviewGate() :> IGate
            BuildTestGate() :> IGate
        ]

    let tryGetGate gateName =
        gates
        |> List.tryFind (fun gate -> gate.Definition.Name.Equals(gateName, StringComparison.OrdinalIgnoreCase))

    let runGate gateName context =
        task {
            match tryGetGate gateName with
            | Some gate -> return! gate.Run context
            | None ->
                return
                    { GateAttestation.Default with
                        GateAttestationId = Guid.NewGuid()
                        CandidateId = context.Candidate.CandidateId
                        OwnerId = context.Candidate.OwnerId
                        OrganizationId = context.Candidate.OrganizationId
                        RepositoryId = context.Candidate.RepositoryId
                        PolicySnapshotId = context.Candidate.PolicySnapshotId
                        BaselineHeadReferenceId = context.Candidate.BaselineHeadReferenceId
                        GateName = gateName
                        GateVersion = "unknown"
                        Result = GateResult.Block
                        Summary = "Gate not registered."
                        Timestamp = getCurrentInstant ()
                        Principal = context.Principal
                    }
        }
