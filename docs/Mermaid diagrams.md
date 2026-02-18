# Mermaid diagrams

## Starting state

```mermaid
%%{init: { 'logLevel': 'debug', 'theme': 'default', 'gitGraph': {'showBranches': true, 'showCommitLabel': false}} }%%
    gitGraph
        commit tag: "ce38fa92"
        branch Scott
        branch Mia
        branch Lorenzo
        checkout Scott
        commit tag: "87923da8: based on ce38fa92"
        checkout Mia
        commit tag: "7d29abac: based on ce38fa92"
        checkout Lorenzo
        commit tag: "28a5c67b: based on ce38fa92"
        checkout main
```

## A promotion on `main`

```mermaid
%%{init: { 'logLevel': 'debug', 'theme': 'default', 'gitGraph': {'showBranches': true, 'showCommitLabel': false}} }%%
    gitGraph
        commit tag: "ce38fa92"
        branch Scott
        branch Mia
        branch Lorenzo
        checkout Scott
        commit tag: "87923da8: based on ce38fa92"
        checkout Mia
        commit tag: "7d29abac: based on ce38fa92"
        checkout Lorenzo
        commit tag: "28a5c67b: based on ce38fa92"
        checkout main
        commit tag: "87923da8"
```

## Branching model

```mermaid
graph TD;
    A[master] -->|Merge| B[release];
    B -->|Merge| C[develop];
    C -->|Merge| D[feature branch];
    D -->|Feature Completed| C;
    B -->|Release Completed| A;
    E[hotfix branch] -->|Fix Applied| A;
    E -->|Fix Merged into| C;

    classDef branch fill:#37f,stroke:#666,stroke-width:3px;
    class A,B,C,D,E branch;
```

## Entities

```mermaid
flowchart LR
  %% Arrows point from "FK holder" --> "referenced DTO".
  %% Labels are the FK field(s). [*] means list/collection. (optional) means option.

  subgraph Identity
    OwnerDto["OwnerDto<br/>PK: OwnerId"]
    OrganizationDto["OrganizationDto<br/>PK: OrganizationId"]
    RepositoryDto["RepositoryDto<br/>PK: RepositoryId"]
  end

  subgraph VersionGraph
    BranchDto["BranchDto<br/>PK: BranchId"]
    ReferenceDto["ReferenceDto<br/>PK: ReferenceId"]
    DirectoryVersionDto["DirectoryVersionDto<br/>PK: DirectoryVersionId"]
    DiffDto["DiffDto<br/>Diff between DirectoryVersionIds"]
  end

  subgraph PolicyReview
    PolicySnapshot["PolicySnapshot<br/>PK: PolicySnapshotId"]
    Stage0Analysis["Stage0Analysis<br/>PK: Stage0AnalysisId"]
    ReviewPacket["ReviewPacket<br/>PK: ReviewPacketId"]
    ReviewCheckpoint["ReviewCheckpoint<br/>PK: ReviewCheckpointId"]
  end

  subgraph PromotionSystem
    PromotionGroupDto["PromotionGroupDto<br/>PK: PromotionGroupId"]
    IntegrationCandidate["IntegrationCandidate<br/>PK: CandidateId"]
    PromotionQueue["PromotionQueue<br/>Key: TargetBranchId"]
    GateAttestation["GateAttestation<br/>PK: GateAttestationId"]
    ConflictReceipt["ConflictReceipt<br/>PK: ConflictReceiptId"]
  end

  subgraph WorkManagement
    WorkItemDto["WorkItemDto<br/>PK: WorkItemId"]
  end

  subgraph Reminders
    ReminderDto["ReminderDto<br/>PK: ReminderId"]
  end

  %% Identity hierarchy / tenancy
  OrganizationDto -->|OwnerId| OwnerDto
  RepositoryDto -->|OwnerId| OwnerDto
  RepositoryDto -->|OrganizationId| OrganizationDto

  %% Branch
  BranchDto -->|OwnerId| OwnerDto
  BranchDto -->|OrganizationId| OrganizationDto
  BranchDto -->|RepositoryId| RepositoryDto
  BranchDto -->|ParentBranchId| BranchDto
  BranchDto -->|BasedOn| ReferenceDto
  BranchDto -->|LatestReference / LatestPromotion / LatestCommit / LatestCheckpoint / LatestSave| ReferenceDto

  %% Reference
  ReferenceDto -->|OwnerId| OwnerDto
  ReferenceDto -->|OrganizationId| OrganizationDto
  ReferenceDto -->|RepositoryId| RepositoryDto
  ReferenceDto -->|BranchId| BranchDto
  ReferenceDto -->|DirectoryId| DirectoryVersionDto
  ReferenceDto -->|Links.BasedOn| ReferenceDto
  ReferenceDto -->|Links.IncludedInPromotionGroup / Links.PromotionGroupTerminal| PromotionGroupDto

  %% DirectoryVersion
  DirectoryVersionDto -->|OwnerId| OwnerDto
  DirectoryVersionDto -->|OrganizationId| OrganizationDto
  DirectoryVersionDto -->|RepositoryId| RepositoryDto
  DirectoryVersionDto -->|DirectoryVersion.Directories| DirectoryVersionDto

  %% Diff
  DiffDto -->|OwnerId| OwnerDto
  DiffDto -->|OrganizationId| OrganizationDto
  DiffDto -->|RepositoryId| RepositoryDto
  DiffDto -->|DirectoryVersionId1| DirectoryVersionDto
  DiffDto -->|DirectoryVersionId2| DirectoryVersionDto

  %% PolicySnapshot
  PolicySnapshot -->|OwnerId| OwnerDto
  PolicySnapshot -->|OrganizationId| OrganizationDto
  PolicySnapshot -->|RepositoryId| RepositoryDto
  PolicySnapshot -->|TargetBranchId| BranchDto

  %% PromotionGroup
  PromotionGroupDto -->|OwnerId| OwnerDto
  PromotionGroupDto -->|OrganizationId| OrganizationDto
  PromotionGroupDto -->|RepositoryId| RepositoryDto
  PromotionGroupDto -->|TargetBranchId| BranchDto
  PromotionGroupDto -->|Promotions.PromotionId| ReferenceDto

  %% PromotionQueue
  PromotionQueue -->|TargetBranchId| BranchDto
  PromotionQueue -->|PolicySnapshotId| PolicySnapshot
  PromotionQueue -->|CandidateIds| IntegrationCandidate
  PromotionQueue -->|RunningCandidateId| IntegrationCandidate

  %% IntegrationCandidate
  IntegrationCandidate -->|OwnerId| OwnerDto
  IntegrationCandidate -->|OrganizationId| OrganizationDto
  IntegrationCandidate -->|RepositoryId| RepositoryDto
  IntegrationCandidate -->|WorkItemId| WorkItemDto
  IntegrationCandidate -->|PromotionGroupId| PromotionGroupDto
  IntegrationCandidate -->|TargetBranchId| BranchDto
  IntegrationCandidate -->|PolicySnapshotId| PolicySnapshot
  IntegrationCandidate -->|BaselineHeadReferenceId| ReferenceDto
  IntegrationCandidate -->|ReviewPacketId| ReviewPacket
  IntegrationCandidate -->|LastCheckpointId| ReviewCheckpoint
  IntegrationCandidate -->|GateAttestationIds| GateAttestation
  IntegrationCandidate -->|ConflictReceiptIds| ConflictReceipt

  %% GateAttestation
  GateAttestation -->|OwnerId| OwnerDto
  GateAttestation -->|OrganizationId| OrganizationDto
  GateAttestation -->|RepositoryId| RepositoryDto
  GateAttestation -->|CandidateId| IntegrationCandidate
  GateAttestation -->|PolicySnapshotId| PolicySnapshot
  GateAttestation -->|BaselineHeadReferenceId| ReferenceDto

  %% ConflictReceipt
  ConflictReceipt -->|OwnerId| OwnerDto
  ConflictReceipt -->|OrganizationId| OrganizationDto
  ConflictReceipt -->|RepositoryId| RepositoryDto
  ConflictReceipt -->|CandidateId| IntegrationCandidate

  %% WorkItem
  WorkItemDto -->|OwnerId| OwnerDto
  WorkItemDto -->|OrganizationId| OrganizationDto
  WorkItemDto -->|RepositoryId| RepositoryDto
  WorkItemDto -->|BranchIds| BranchDto
  WorkItemDto -->|ReferenceIds| ReferenceDto
  WorkItemDto -->|PromotionGroupIds| PromotionGroupDto
  WorkItemDto -->|CandidateIds| IntegrationCandidate
  WorkItemDto -->|ReviewPacketIds| ReviewPacket
  WorkItemDto -->|ReviewCheckpointIds| ReviewCheckpoint
  WorkItemDto -->|GateAttestationIds| GateAttestation

  %% ReviewPacket
  ReviewPacket -->|OwnerId| OwnerDto
  ReviewPacket -->|OrganizationId| OrganizationDto
  ReviewPacket -->|RepositoryId| RepositoryDto
  ReviewPacket -->|CandidateId| IntegrationCandidate
  ReviewPacket -->|PromotionGroupId| PromotionGroupDto
  ReviewPacket -->|PolicySnapshotId| PolicySnapshot
  ReviewPacket -->|GateSummary.GateAttestationIds| GateAttestation

  %% ReviewCheckpoint
  ReviewCheckpoint -->|CandidateId| IntegrationCandidate
  ReviewCheckpoint -->|PromotionGroupId| PromotionGroupDto
  ReviewCheckpoint -->|ReviewedUpToReferenceId| ReferenceDto
  ReviewCheckpoint -->|PolicySnapshotId| PolicySnapshot

  %% Stage0Analysis
  Stage0Analysis -->|OwnerId| OwnerDto
  Stage0Analysis -->|OrganizationId| OrganizationDto
  Stage0Analysis -->|RepositoryId| RepositoryDto
  Stage0Analysis -->|ReferenceId| ReferenceDto
  Stage0Analysis -->|WorkItemId| WorkItemDto
  Stage0Analysis -->|CandidateId| IntegrationCandidate
  Stage0Analysis -->|PolicySnapshotId| PolicySnapshot

  %% Reminder
  ReminderDto -->|OwnerId| OwnerDto
  ReminderDto -->|OrganizationId| OrganizationDto
  ReminderDto -->|RepositoryId| RepositoryDto

```