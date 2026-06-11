# @grace-vcs/generated-openapi-probe@0.0.0-private

A TypeScript SDK client for the localhost API.

## Usage

First, install the SDK from npm.

```bash
npm install @grace-vcs/generated-openapi-probe --save
```

Next, try it out.


```ts
import {
  Configuration,
  ApprovalsApi,
} from '@grace-vcs/generated-openapi-probe';
import type { ApprovalRequestHistoryRequest } from '@grace-vcs/generated-openapi-probe';

async function example() {
  console.log("🚀 Testing @grace-vcs/generated-openapi-probe SDK...");
  const config = new Configuration({ 
    // Configure HTTP bearer authorization: bearerAuth
    accessToken: "YOUR BEARER TOKEN",
  });
  const api = new ApprovalsApi(config);

  const body = {
    // ApprovalRequestParameters
    approvalRequestParameters: ...,
  } satisfies ApprovalRequestHistoryRequest;

  try {
    const data = await api.approvalRequestHistory(body);
    console.log(data);
  } catch (error) {
    console.error(error);
  }
}

// Run the test
example().catch(console.error);
```


## Documentation

### API Endpoints

All URIs are relative to *http://localhost:5000*

| Class | Method | HTTP request | Description
| ----- | ------ | ------------ | -------------
*ApprovalsApi* | [**approvalRequestHistory**](docs/ApprovalsApi.md#approvalrequesthistory) | **POST** /approval/request/history | Show approval request history.
*ApprovalsApi* | [**approveApprovalRequest**](docs/ApprovalsApi.md#approveapprovalrequest) | **POST** /approval/request/approve | Approve a workflow-generated approval request.
*ApprovalsApi* | [**createApprovalPolicy**](docs/ApprovalsApi.md#createapprovalpolicy) | **POST** /approval/policy/create | Create an approval policy.
*ApprovalsApi* | [**deleteApprovalPolicy**](docs/ApprovalsApi.md#deleteapprovalpolicy) | **POST** /approval/policy/delete | Delete an approval policy.
*ApprovalsApi* | [**disableApprovalPolicy**](docs/ApprovalsApi.md#disableapprovalpolicy) | **POST** /approval/policy/disable | Disable an approval policy.
*ApprovalsApi* | [**enableApprovalPolicy**](docs/ApprovalsApi.md#enableapprovalpolicy) | **POST** /approval/policy/enable | Enable an approval policy.
*ApprovalsApi* | [**evaluateApprovalPolicy**](docs/ApprovalsApi.md#evaluateapprovalpolicy) | **POST** /approval/policy/evaluate | Evaluate approval policies for a subject.
*ApprovalsApi* | [**listApprovalPolicies**](docs/ApprovalsApi.md#listapprovalpolicies) | **POST** /approval/policy/list | List approval policies.
*ApprovalsApi* | [**listApprovalRequests**](docs/ApprovalsApi.md#listapprovalrequests) | **POST** /approval/request/list | List workflow-generated approval requests.
*ApprovalsApi* | [**rejectApprovalRequest**](docs/ApprovalsApi.md#rejectapprovalrequest) | **POST** /approval/request/reject | Reject a workflow-generated approval request.
*ApprovalsApi* | [**showApprovalPolicy**](docs/ApprovalsApi.md#showapprovalpolicy) | **POST** /approval/policy/show | Show an approval policy.
*ApprovalsApi* | [**showApprovalRequest**](docs/ApprovalsApi.md#showapprovalrequest) | **POST** /approval/request/show | Show a workflow-generated approval request.
*ApprovalsApi* | [**updateApprovalPolicy**](docs/ApprovalsApi.md#updateapprovalpolicy) | **POST** /approval/policy/update | Update an approval policy.
*BranchesApi* | [**annotateBranch**](docs/BranchesApi.md#annotatebranch) | **POST** /branch/annotate | Annotate a branch reference.
*BranchesApi* | [**checkpointBranch**](docs/BranchesApi.md#checkpointbranch) | **POST** /branch/checkpoint | Checkpoint the current branch content.
*BranchesApi* | [**commitBranch**](docs/BranchesApi.md#commitbranch) | **POST** /branch/commit | Commit the current branch content.
*BranchesApi* | [**createBranch**](docs/BranchesApi.md#createbranch) | **POST** /branch/create | Create a branch.
*BranchesApi* | [**deleteBranch**](docs/BranchesApi.md#deletebranch) | **POST** /branch/delete | Delete a branch.
*BranchesApi* | [**enableBranchCheckpoint**](docs/BranchesApi.md#enablebranchcheckpoint) | **POST** /branch/enableCheckpoint | Enable or disable checkpoint references.
*BranchesApi* | [**enableBranchCommit**](docs/BranchesApi.md#enablebranchcommit) | **POST** /branch/enableCommit | Enable or disable commit references.
*BranchesApi* | [**enableBranchPromotion**](docs/BranchesApi.md#enablebranchpromotion) | **POST** /branch/enablePromotion | Enable or disable promotion references.
*BranchesApi* | [**enableBranchSave**](docs/BranchesApi.md#enablebranchsave) | **POST** /branch/enableSave | Enable or disable save references.
*BranchesApi* | [**enableBranchTag**](docs/BranchesApi.md#enablebranchtag) | **POST** /branch/enableTag | Enable or disable tag references.
*BranchesApi* | [**getBranch**](docs/BranchesApi.md#getbranch) | **POST** /branch/get | Get a branch.
*BranchesApi* | [**getBranchReference**](docs/BranchesApi.md#getbranchreference) | **POST** /branch/getReference | Get a branch reference.
*BranchesApi* | [**getParentBranch**](docs/BranchesApi.md#getparentbranch) | **POST** /branch/getParentBranch | Get the parent branch.
*BranchesApi* | [**listBranchCheckpoints**](docs/BranchesApi.md#listbranchcheckpoints) | **POST** /branch/getCheckpoints | List branch checkpoints.
*BranchesApi* | [**listBranchCommits**](docs/BranchesApi.md#listbranchcommits) | **POST** /branch/getCommits | List branch commits.
*BranchesApi* | [**listBranchPromotions**](docs/BranchesApi.md#listbranchpromotions) | **POST** /branch/getPromotions | List branch promotions.
*BranchesApi* | [**listBranchReferences**](docs/BranchesApi.md#listbranchreferences) | **POST** /branch/getReferences | List branch references.
*BranchesApi* | [**listBranchSaves**](docs/BranchesApi.md#listbranchsaves) | **POST** /branch/getSaves | List branch saves.
*BranchesApi* | [**listBranchTags**](docs/BranchesApi.md#listbranchtags) | **POST** /branch/getTags | List branch tags.
*BranchesApi* | [**promoteBranch**](docs/BranchesApi.md#promotebranch) | **POST** /branch/promote | Promote the current branch content.
*BranchesApi* | [**rebaseBranch**](docs/BranchesApi.md#rebasebranch) | **POST** /branch/rebase | Rebase a branch.
*BranchesApi* | [**saveBranch**](docs/BranchesApi.md#savebranch) | **POST** /branch/save | Save the current branch content.
*BranchesApi* | [**tagBranch**](docs/BranchesApi.md#tagbranch) | **POST** /branch/tag | Tag the current branch content.
*DefaultApi* | [**claimReuseRanges**](docs/DefaultApi.md#claimreuseranges) | **POST** /storage/claimReuseRanges | Claims reusable ContentBlock ranges.
*DefaultApi* | [**confirmContentBlockUpload**](docs/DefaultApi.md#confirmcontentblockupload) | **POST** /storage/confirmContentBlockUpload | Confirms a ContentBlock upload.
*DefaultApi* | [**discoverContentBlocks**](docs/DefaultApi.md#discovercontentblocks) | **POST** /storage/discoverContentBlocks | Discovers reusable ContentBlock candidates.
*DefaultApi* | [**finalizeManifestUpload**](docs/DefaultApi.md#finalizemanifestupload) | **POST** /storage/finalizeManifestUpload | Finalizes a manifest upload.
*DefaultApi* | [**getContentBlockDownloadUri**](docs/DefaultApi.md#getcontentblockdownloaduri) | **POST** /storage/getContentBlockDownloadUri | Gets a download URI for a ContentBlock payload.
*DefaultApi* | [**getContentBlockUploadUri**](docs/DefaultApi.md#getcontentblockuploaduri) | **POST** /storage/getContentBlockUploadUri | Gets an upload URI for a ContentBlock payload.
*DefaultApi* | [**getDownloadUri**](docs/DefaultApi.md#getdownloaduri) | **POST** /storage/getDownloadUri | Gets a download URI for whole-file content.
*DefaultApi* | [**getOpenApi**](docs/DefaultApi.md#getopenapi) | **GET** /openApi | Get the OpenAPI specification for the Grace Server API
*DefaultApi* | [**getUploadMetadataForFiles**](docs/DefaultApi.md#getuploadmetadataforfiles) | **POST** /storage/getUploadMetadataForFiles | Gets upload metadata for files.
*DefaultApi* | [**getUploadUri**](docs/DefaultApi.md#getuploaduri) | **POST** /storage/getUploadUri | Gets upload URIs for whole-file content.
*DefaultApi* | [**issueDedupeDiscovery**](docs/DefaultApi.md#issuededupediscovery) | **POST** /storage/issueDedupeDiscovery | Issues upload-session dedupe discovery hints.
*DefaultApi* | [**registerContentBlockUpload**](docs/DefaultApi.md#registercontentblockupload) | **POST** /storage/registerContentBlockUpload | Registers a ContentBlock upload intent.
*DefaultApi* | [**startManifestUploadSession**](docs/DefaultApi.md#startmanifestuploadsession) | **POST** /storage/startManifestUploadSession | Starts a manifest upload session.
*DiffsApi* | [**getDiff**](docs/DiffsApi.md#getdiff) | **POST** /diff/getDiff | Get a diff.
*DiffsApi* | [**getDiffByBlake3Hash**](docs/DiffsApi.md#getdiffbyblake3hash) | **POST** /diff/getDiffByBlake3Hash | Get a diff by BLAKE3 hash.
*DiffsApi* | [**getDiffBySha256Hash**](docs/DiffsApi.md#getdiffbysha256hash) | **POST** /diff/getDiffBySha256Hash | Get a diff by SHA-256 hash.
*DiffsApi* | [**populateDiff**](docs/DiffsApi.md#populatediff) | **POST** /diff/populate | Populate a diff actor.
*DirectoriesApi* | [**createDirectoryVersion**](docs/DirectoriesApi.md#createdirectoryversion) | **POST** /directory/create | Create a new directory version.
*DirectoriesApi* | [**getDirectoryVersion**](docs/DirectoriesApi.md#getdirectoryversion) | **POST** /directory/get | Get a directory version.
*DirectoriesApi* | [**getDirectoryVersionByBlake3Hash**](docs/DirectoriesApi.md#getdirectoryversionbyblake3hash) | **POST** /directory/getByBlake3Hash | Get a directory version by BLAKE3 hash.
*DirectoriesApi* | [**getDirectoryVersionBySha256Hash**](docs/DirectoriesApi.md#getdirectoryversionbysha256hash) | **POST** /directory/getBySha256Hash | Get a directory version by SHA-256 hash.
*DirectoriesApi* | [**listDirectoryVersionsById**](docs/DirectoriesApi.md#listdirectoryversionsbyid) | **POST** /directory/getByDirectoryIds | List directory versions by id.
*DirectoriesApi* | [**listDirectoryVersionsRecursive**](docs/DirectoriesApi.md#listdirectoryversionsrecursive) | **POST** /directory/getDirectoryVersionsRecursive | List a directory version and its children.
*DirectoriesApi* | [**saveDirectoryVersions**](docs/DirectoriesApi.md#savedirectoryversions) | **POST** /directory/saveDirectoryVersions | Save directory versions.
*OrganizationsApi* | [**createOrganization**](docs/OrganizationsApi.md#createorganization) | **POST** /organization/create | Create an organization.
*OrganizationsApi* | [**deleteOrganization**](docs/OrganizationsApi.md#deleteorganization) | **POST** /organization/delete | Delete an organization.
*OrganizationsApi* | [**getOrganization**](docs/OrganizationsApi.md#getorganization) | **POST** /organization/get | Get an organization.
*OrganizationsApi* | [**listOrganizationRepositories**](docs/OrganizationsApi.md#listorganizationrepositories) | **POST** /organization/listRepositories | List repositories of an organization.
*OrganizationsApi* | [**setOrganizationDescription**](docs/OrganizationsApi.md#setorganizationdescription) | **POST** /organization/setDescription | Set the organization\&#39;s description.
*OrganizationsApi* | [**setOrganizationName**](docs/OrganizationsApi.md#setorganizationname) | **POST** /organization/setName | Set the name of an organization.
*OrganizationsApi* | [**setOrganizationSearchVisibility**](docs/OrganizationsApi.md#setorganizationsearchvisibility) | **POST** /organization/setSearchVisibility | Set organization search visibility.
*OrganizationsApi* | [**setOrganizationType**](docs/OrganizationsApi.md#setorganizationtype) | **POST** /organization/setType | Set the organization type.
*OrganizationsApi* | [**undeleteOrganization**](docs/OrganizationsApi.md#undeleteorganization) | **POST** /organization/undelete | Undelete a previously deleted organization.
*OwnersApi* | [**createOwner**](docs/OwnersApi.md#createowner) | **POST** /owner/create | Create an owner.
*OwnersApi* | [**deleteOwner**](docs/OwnersApi.md#deleteowner) | **POST** /owner/delete | Delete an owner.
*OwnersApi* | [**getOwner**](docs/OwnersApi.md#getowner) | **POST** /owner/get | Get an owner.
*OwnersApi* | [**listOwnerOrganizations**](docs/OwnersApi.md#listownerorganizations) | **POST** /owner/listOrganizations | List the organizations for an owner.
*OwnersApi* | [**setOwnerDescription**](docs/OwnersApi.md#setownerdescription) | **POST** /owner/setDescription | Set the owner\&#39;s description.
*OwnersApi* | [**setOwnerName**](docs/OwnersApi.md#setownername) | **POST** /owner/setName | Set the name of an owner.
*OwnersApi* | [**setOwnerSearchVisibility**](docs/OwnersApi.md#setownersearchvisibility) | **POST** /owner/setSearchVisibility | Set owner search visibility.
*OwnersApi* | [**setOwnerType**](docs/OwnersApi.md#setownertype) | **POST** /owner/setType | Set the owner type.
*OwnersApi* | [**undeleteOwner**](docs/OwnersApi.md#undeleteowner) | **POST** /owner/undelete | Undelete a previously deleted owner.
*RepositoriesApi* | [**createRepository**](docs/RepositoriesApi.md#createrepository) | **POST** /repository/create | Create a new repository.
*RepositoriesApi* | [**deleteRepository**](docs/RepositoriesApi.md#deleterepository) | **POST** /repository/delete | Delete a repository.
*RepositoriesApi* | [**getRepository**](docs/RepositoriesApi.md#getrepository) | **POST** /repository/get | Get a repository.
*RepositoriesApi* | [**listRepositoryBranches**](docs/RepositoriesApi.md#listrepositorybranches) | **POST** /repository/getBranches | List repository branches.
*RepositoriesApi* | [**listRepositoryBranchesByBranchId**](docs/RepositoriesApi.md#listrepositorybranchesbybranchid) | **POST** /repository/getBranchesByBranchId | List branches by branch id.
*RepositoriesApi* | [**listRepositoryReferencesByReferenceId**](docs/RepositoriesApi.md#listrepositoryreferencesbyreferenceid) | **POST** /repository/getReferencesByReferenceId | List references by reference id.
*RepositoriesApi* | [**repositoryExists**](docs/RepositoriesApi.md#repositoryexists) | **POST** /repository/exists | Check whether a repository exists.
*RepositoriesApi* | [**repositoryIsEmpty**](docs/RepositoriesApi.md#repositoryisempty) | **POST** /repository/isEmpty | Check whether a repository is empty.
*RepositoriesApi* | [**setRepositoryCheckpointDays**](docs/RepositoriesApi.md#setrepositorycheckpointdays) | **POST** /repository/setCheckpointDays | Set checkpoint retention.
*RepositoriesApi* | [**setRepositoryDefaultServerApiVersion**](docs/RepositoriesApi.md#setrepositorydefaultserverapiversion) | **POST** /repository/setDefaultServerApiVersion | Set the default server API version.
*RepositoriesApi* | [**setRepositoryDescription**](docs/RepositoriesApi.md#setrepositorydescription) | **POST** /repository/setDescription | Set repository description.
*RepositoriesApi* | [**setRepositoryName**](docs/RepositoriesApi.md#setrepositoryname) | **POST** /repository/setName | Set the name of a repository.
*RepositoriesApi* | [**setRepositoryRecordSaves**](docs/RepositoriesApi.md#setrepositoryrecordsaves) | **POST** /repository/setRecordSaves | Set whether saves are recorded.
*RepositoriesApi* | [**setRepositorySaveDays**](docs/RepositoriesApi.md#setrepositorysavedays) | **POST** /repository/setSaveDays | Set save retention.
*RepositoriesApi* | [**setRepositoryStatus**](docs/RepositoriesApi.md#setrepositorystatus) | **POST** /repository/setStatus | Set repository status.
*RepositoriesApi* | [**setRepositoryVisibility**](docs/RepositoriesApi.md#setrepositoryvisibility) | **POST** /repository/setVisibility | Set repository visibility.
*RepositoriesApi* | [**undeleteRepository**](docs/RepositoriesApi.md#undeleterepository) | **POST** /repository/undelete | Undelete a previously deleted repository.
*WebhooksApi* | [**createWebhookRule**](docs/WebhooksApi.md#createwebhookrule) | **POST** /webhook/rule/create | Create a webhook rule.
*WebhooksApi* | [**deleteWebhookRule**](docs/WebhooksApi.md#deletewebhookrule) | **POST** /webhook/rule/delete | Delete a webhook rule.
*WebhooksApi* | [**disableWebhookRule**](docs/WebhooksApi.md#disablewebhookrule) | **POST** /webhook/rule/disable | Disable a webhook rule.
*WebhooksApi* | [**enableWebhookRule**](docs/WebhooksApi.md#enablewebhookrule) | **POST** /webhook/rule/enable | Enable a webhook rule.
*WebhooksApi* | [**listWebhookDeliveries**](docs/WebhooksApi.md#listwebhookdeliveries) | **POST** /webhook/delivery/list | List webhook deliveries.
*WebhooksApi* | [**listWebhookRules**](docs/WebhooksApi.md#listwebhookrules) | **POST** /webhook/rule/list | List webhook rules.
*WebhooksApi* | [**showWebhookDelivery**](docs/WebhooksApi.md#showwebhookdelivery) | **POST** /webhook/delivery/show | Show a webhook delivery.
*WebhooksApi* | [**showWebhookRule**](docs/WebhooksApi.md#showwebhookrule) | **POST** /webhook/rule/show | Show a webhook rule.
*WebhooksApi* | [**testWebhookRule**](docs/WebhooksApi.md#testwebhookrule) | **POST** /webhook/rule/test | Create a test webhook delivery.
*WebhooksApi* | [**updateWebhookRule**](docs/WebhooksApi.md#updatewebhookrule) | **POST** /webhook/rule/update | Update a webhook rule.


### Models

- [AnnotateParameters](docs/AnnotateParameters.md)
- [AnnotationBoundary](docs/AnnotationBoundary.md)
- [AnnotationLine](docs/AnnotationLine.md)
- [AnnotationLineRange](docs/AnnotationLineRange.md)
- [AnnotationSourceReference](docs/AnnotationSourceReference.md)
- [AnnotationSourceRow](docs/AnnotationSourceRow.md)
- [AnnotationSpan](docs/AnnotationSpan.md)
- [ApprovalDecision](docs/ApprovalDecision.md)
- [ApprovalPolicy](docs/ApprovalPolicy.md)
- [ApprovalPolicyParameters](docs/ApprovalPolicyParameters.md)
- [ApprovalPolicyStatus](docs/ApprovalPolicyStatus.md)
- [ApprovalRequest](docs/ApprovalRequest.md)
- [ApprovalRequestDecision](docs/ApprovalRequestDecision.md)
- [ApprovalRequestParameters](docs/ApprovalRequestParameters.md)
- [ApprovalRequestStatus](docs/ApprovalRequestStatus.md)
- [ApprovalScope](docs/ApprovalScope.md)
- [ApprovalTimeoutAction](docs/ApprovalTimeoutAction.md)
- [ApproveApprovalRequestParameters](docs/ApproveApprovalRequestParameters.md)
- [BlockUploadIntent](docs/BlockUploadIntent.md)
- [BranchAnnotationApiDto](docs/BranchAnnotationApiDto.md)
- [BranchAnnotationReturnValue](docs/BranchAnnotationReturnValue.md)
- [BranchApiDto](docs/BranchApiDto.md)
- [BranchCommandReturnValue](docs/BranchCommandReturnValue.md)
- [BranchDto](docs/BranchDto.md)
- [BranchDtoUpdatedAt](docs/BranchDtoUpdatedAt.md)
- [BranchHashQueryParameters](docs/BranchHashQueryParameters.md)
- [BranchParameters](docs/BranchParameters.md)
- [BranchQueryParameters](docs/BranchQueryParameters.md)
- [BranchReturnValue](docs/BranchReturnValue.md)
- [ChangeType](docs/ChangeType.md)
- [ClaimReuseRangesParameters](docs/ClaimReuseRangesParameters.md)
- [ClaimedReuseRange](docs/ClaimedReuseRange.md)
- [CommonParameters](docs/CommonParameters.md)
- [ConfirmContentBlockUploadParameters](docs/ConfirmContentBlockUploadParameters.md)
- [ConfirmedBlockUpload](docs/ConfirmedBlockUpload.md)
- [ContentBlock](docs/ContentBlock.md)
- [ContentBlockDiscoveryCandidate](docs/ContentBlockDiscoveryCandidate.md)
- [ContentBlockDiscoveryPolicy](docs/ContentBlockDiscoveryPolicy.md)
- [ContentBlockMetadataRange](docs/ContentBlockMetadataRange.md)
- [ContentBlockReuseRangeHint](docs/ContentBlockReuseRangeHint.md)
- [ContentBlockStoragePlacement](docs/ContentBlockStoragePlacement.md)
- [CreateApprovalPolicyParameters](docs/CreateApprovalPolicyParameters.md)
- [CreateBranchParameters](docs/CreateBranchParameters.md)
- [CreateOrganizationParameters](docs/CreateOrganizationParameters.md)
- [CreateOwnerParameters](docs/CreateOwnerParameters.md)
- [CreateParameters](docs/CreateParameters.md)
- [CreateReferenceParameters](docs/CreateReferenceParameters.md)
- [CreateRepositoryParameters](docs/CreateRepositoryParameters.md)
- [CreateWebhookRuleParameters](docs/CreateWebhookRuleParameters.md)
- [DedupeDiscoverySnapshot](docs/DedupeDiscoverySnapshot.md)
- [DeleteBranchParameters](docs/DeleteBranchParameters.md)
- [DeleteOrganizationParameters](docs/DeleteOrganizationParameters.md)
- [DeleteOwnerParameters](docs/DeleteOwnerParameters.md)
- [DeleteRepositoryParameters](docs/DeleteRepositoryParameters.md)
- [DiffApiDto](docs/DiffApiDto.md)
- [DiffDto](docs/DiffDto.md)
- [DiffParameters](docs/DiffParameters.md)
- [DiffPiece](docs/DiffPiece.md)
- [DiffPopulateReturnValue](docs/DiffPopulateReturnValue.md)
- [DiffReturnValue](docs/DiffReturnValue.md)
- [DifferenceType](docs/DifferenceType.md)
- [DirectoryCommandReturnValue](docs/DirectoryCommandReturnValue.md)
- [DirectoryParameters](docs/DirectoryParameters.md)
- [DirectoryVersion](docs/DirectoryVersion.md)
- [DirectoryVersionApiDto](docs/DirectoryVersionApiDto.md)
- [DirectoryVersionHashLookupReturnValue](docs/DirectoryVersionHashLookupReturnValue.md)
- [DirectoryVersionListReturnValue](docs/DirectoryVersionListReturnValue.md)
- [DirectoryVersionReturnValue](docs/DirectoryVersionReturnValue.md)
- [DiscoverContentBlocksParameters](docs/DiscoverContentBlocksParameters.md)
- [DiscoverContentBlocksResult](docs/DiscoverContentBlocksResult.md)
- [DiscoverContentBlocksReturnValue](docs/DiscoverContentBlocksReturnValue.md)
- [EnableFeatureParameters](docs/EnableFeatureParameters.md)
- [EnablePromotionTypeParameters](docs/EnablePromotionTypeParameters.md)
- [EvaluateApprovalPolicyParameters](docs/EvaluateApprovalPolicyParameters.md)
- [EventMetadata](docs/EventMetadata.md)
- [FileContentReference](docs/FileContentReference.md)
- [FileDiff](docs/FileDiff.md)
- [FileManifest](docs/FileManifest.md)
- [FileSystemDifference](docs/FileSystemDifference.md)
- [FileSystemEntryType](docs/FileSystemEntryType.md)
- [FileVersion](docs/FileVersion.md)
- [FinalizeManifestBlockPayload](docs/FinalizeManifestBlockPayload.md)
- [FinalizeManifestUploadParameters](docs/FinalizeManifestUploadParameters.md)
- [GetBranchParameters](docs/GetBranchParameters.md)
- [GetBranchVersionParameters](docs/GetBranchVersionParameters.md)
- [GetBranchesByBranchIdParameters](docs/GetBranchesByBranchIdParameters.md)
- [GetBranchesParameters](docs/GetBranchesParameters.md)
- [GetByBlake3HashParameters](docs/GetByBlake3HashParameters.md)
- [GetByDirectoryIdsParameters](docs/GetByDirectoryIdsParameters.md)
- [GetBySha256HashParameters](docs/GetBySha256HashParameters.md)
- [GetContentBlockDownloadUriParameters](docs/GetContentBlockDownloadUriParameters.md)
- [GetContentBlockUploadUriParameters](docs/GetContentBlockUploadUriParameters.md)
- [GetDiffByBlake3HashParameters](docs/GetDiffByBlake3HashParameters.md)
- [GetDiffByReferenceTypeParameters](docs/GetDiffByReferenceTypeParameters.md)
- [GetDiffBySha256HashParameters](docs/GetDiffBySha256HashParameters.md)
- [GetDiffParameters](docs/GetDiffParameters.md)
- [GetDiffsForReferenceTypeParameters](docs/GetDiffsForReferenceTypeParameters.md)
- [GetDiffsForReferencesParameters](docs/GetDiffsForReferencesParameters.md)
- [GetDownloadUriParameters](docs/GetDownloadUriParameters.md)
- [GetOrganizationParameters](docs/GetOrganizationParameters.md)
- [GetOwnerParameters](docs/GetOwnerParameters.md)
- [GetParameters](docs/GetParameters.md)
- [GetReferenceParameters](docs/GetReferenceParameters.md)
- [GetReferencesByReferenceIdParameters](docs/GetReferencesByReferenceIdParameters.md)
- [GetReferencesParameters](docs/GetReferencesParameters.md)
- [GetUploadMetadataForFilesParameters](docs/GetUploadMetadataForFilesParameters.md)
- [GetUploadUriParameters](docs/GetUploadUriParameters.md)
- [GraceError](docs/GraceError.md)
- [GraceResult](docs/GraceResult.md)
- [GraceReturnValue](docs/GraceReturnValue.md)
- [GraceReturnValueBase](docs/GraceReturnValueBase.md)
- [InitParameters](docs/InitParameters.md)
- [InlineObject](docs/InlineObject.md)
- [InlineObject1](docs/InlineObject1.md)
- [InlineObject2](docs/InlineObject2.md)
- [InlineObject3](docs/InlineObject3.md)
- [InlineObject4](docs/InlineObject4.md)
- [InlineObject5](docs/InlineObject5.md)
- [InlineObject6](docs/InlineObject6.md)
- [InlineObject7](docs/InlineObject7.md)
- [IsEmptyParameters](docs/IsEmptyParameters.md)
- [IssueDedupeDiscoveryParameters](docs/IssueDedupeDiscoveryParameters.md)
- [ListApprovalPoliciesParameters](docs/ListApprovalPoliciesParameters.md)
- [ListApprovalRequestsParameters](docs/ListApprovalRequestsParameters.md)
- [ListOrganizationsParameters](docs/ListOrganizationsParameters.md)
- [ListRepositoriesParameters](docs/ListRepositoriesParameters.md)
- [ListWebhookDeliveriesParameters](docs/ListWebhookDeliveriesParameters.md)
- [ListWebhookRulesParameters](docs/ListWebhookRulesParameters.md)
- [ObjectStorageProvider](docs/ObjectStorageProvider.md)
- [OrganizationCommandReturnValue](docs/OrganizationCommandReturnValue.md)
- [OrganizationDto](docs/OrganizationDto.md)
- [OrganizationParameters](docs/OrganizationParameters.md)
- [OrganizationReturnValue](docs/OrganizationReturnValue.md)
- [OrganizationType](docs/OrganizationType.md)
- [OutboundUrlSafety](docs/OutboundUrlSafety.md)
- [OwnerCommandReturnValue](docs/OwnerCommandReturnValue.md)
- [OwnerDto](docs/OwnerDto.md)
- [OwnerParameters](docs/OwnerParameters.md)
- [OwnerReturnValue](docs/OwnerReturnValue.md)
- [OwnerType](docs/OwnerType.md)
- [PopulateParameters](docs/PopulateParameters.md)
- [ProblemDetails](docs/ProblemDetails.md)
- [PromotionSetApprovalState](docs/PromotionSetApprovalState.md)
- [PromotionSetApprovalSummary](docs/PromotionSetApprovalSummary.md)
- [RebaseParameters](docs/RebaseParameters.md)
- [RecordSavesParameters](docs/RecordSavesParameters.md)
- [ReferenceApiDto](docs/ReferenceApiDto.md)
- [ReferenceDto](docs/ReferenceDto.md)
- [ReferenceListReturnValue](docs/ReferenceListReturnValue.md)
- [ReferenceParameters](docs/ReferenceParameters.md)
- [ReferenceReturnValue](docs/ReferenceReturnValue.md)
- [ReferenceType](docs/ReferenceType.md)
- [RegisterContentBlockUploadParameters](docs/RegisterContentBlockUploadParameters.md)
- [RepositoryBooleanReturnValue](docs/RepositoryBooleanReturnValue.md)
- [RepositoryBranchesReturnValue](docs/RepositoryBranchesReturnValue.md)
- [RepositoryCommandReturnValue](docs/RepositoryCommandReturnValue.md)
- [RepositoryDto](docs/RepositoryDto.md)
- [RepositoryParameters](docs/RepositoryParameters.md)
- [RepositoryReferencesReturnValue](docs/RepositoryReferencesReturnValue.md)
- [RepositoryReturnValue](docs/RepositoryReturnValue.md)
- [RepositoryStatus](docs/RepositoryStatus.md)
- [RepositoryVisibility](docs/RepositoryVisibility.md)
- [SaveDirectoryVersionsParameters](docs/SaveDirectoryVersionsParameters.md)
- [ScopedOutboundUrl](docs/ScopedOutboundUrl.md)
- [SearchVisibility](docs/SearchVisibility.md)
- [SetBranchNameParameters](docs/SetBranchNameParameters.md)
- [SetCheckpointDaysParameters](docs/SetCheckpointDaysParameters.md)
- [SetDefaultServerApiVersionParameters](docs/SetDefaultServerApiVersionParameters.md)
- [SetOrganizationDescriptionParameters](docs/SetOrganizationDescriptionParameters.md)
- [SetOrganizationNameParameters](docs/SetOrganizationNameParameters.md)
- [SetOrganizationSearchVisibilityParameters](docs/SetOrganizationSearchVisibilityParameters.md)
- [SetOrganizationTypeParameters](docs/SetOrganizationTypeParameters.md)
- [SetOwnerDescriptionParameters](docs/SetOwnerDescriptionParameters.md)
- [SetOwnerNameParameters](docs/SetOwnerNameParameters.md)
- [SetOwnerSearchVisibilityParameters](docs/SetOwnerSearchVisibilityParameters.md)
- [SetOwnerTypeParameters](docs/SetOwnerTypeParameters.md)
- [SetRepositoryDescriptionParameters](docs/SetRepositoryDescriptionParameters.md)
- [SetRepositoryNameParameters](docs/SetRepositoryNameParameters.md)
- [SetRepositoryStatusParameters](docs/SetRepositoryStatusParameters.md)
- [SetRepositoryVisibilityParameters](docs/SetRepositoryVisibilityParameters.md)
- [SetSaveDaysParameters](docs/SetSaveDaysParameters.md)
- [StartManifestUploadSessionParameters](docs/StartManifestUploadSessionParameters.md)
- [StartUploadSession](docs/StartUploadSession.md)
- [StorageParameters](docs/StorageParameters.md)
- [SwitchParameters](docs/SwitchParameters.md)
- [TestWebhookRuleParameters](docs/TestWebhookRuleParameters.md)
- [UndeleteOrganizationParameters](docs/UndeleteOrganizationParameters.md)
- [UndeleteOwnerParameters](docs/UndeleteOwnerParameters.md)
- [UndeleteRepositoryParameters](docs/UndeleteRepositoryParameters.md)
- [UploadMetadata](docs/UploadMetadata.md)
- [UploadMetadataArrayReturnValue](docs/UploadMetadataArrayReturnValue.md)
- [UploadSessionBlockUploadConfirmedEvent](docs/UploadSessionBlockUploadConfirmedEvent.md)
- [UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed](docs/UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.md)
- [UploadSessionBlockUploadIntentRegisteredEvent](docs/UploadSessionBlockUploadIntentRegisteredEvent.md)
- [UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered](docs/UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered.md)
- [UploadSessionCleanupReminderScheduledEvent](docs/UploadSessionCleanupReminderScheduledEvent.md)
- [UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled](docs/UploadSessionCleanupReminderScheduledEventCleanupReminderScheduled.md)
- [UploadSessionDecision](docs/UploadSessionDecision.md)
- [UploadSessionDecisionReturnValue](docs/UploadSessionDecisionReturnValue.md)
- [UploadSessionDedupeDiscoveryIssuedEvent](docs/UploadSessionDedupeDiscoveryIssuedEvent.md)
- [UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued](docs/UploadSessionDedupeDiscoveryIssuedEventDedupeDiscoveryIssued.md)
- [UploadSessionDto](docs/UploadSessionDto.md)
- [UploadSessionEvent](docs/UploadSessionEvent.md)
- [UploadSessionEventType](docs/UploadSessionEventType.md)
- [UploadSessionFinalizedEvent](docs/UploadSessionFinalizedEvent.md)
- [UploadSessionFinalizedEventFinalized](docs/UploadSessionFinalizedEventFinalized.md)
- [UploadSessionLifecycleState](docs/UploadSessionLifecycleState.md)
- [UploadSessionOperationEvent](docs/UploadSessionOperationEvent.md)
- [UploadSessionReuseRangesClaimedEvent](docs/UploadSessionReuseRangesClaimedEvent.md)
- [UploadSessionReuseRangesClaimedEventReuseRangesClaimed](docs/UploadSessionReuseRangesClaimedEventReuseRangesClaimed.md)
- [UploadSessionStartedEvent](docs/UploadSessionStartedEvent.md)
- [UploadSessionStorageParameters](docs/UploadSessionStorageParameters.md)
- [WebhookDelivery](docs/WebhookDelivery.md)
- [WebhookDeliveryParameters](docs/WebhookDeliveryParameters.md)
- [WebhookDeliveryStatus](docs/WebhookDeliveryStatus.md)
- [WebhookRetryPolicy](docs/WebhookRetryPolicy.md)
- [WebhookRule](docs/WebhookRule.md)
- [WebhookRuleParameters](docs/WebhookRuleParameters.md)
- [WebhookRuleStatus](docs/WebhookRuleStatus.md)
- [WebhookScope](docs/WebhookScope.md)

### Authorization


Authentication schemes defined for the API:
<a id="api_key"></a>
#### api_key


- **Type**: API key
- **API key parameter name**: `X-API-Key`
- **Location**: HTTP header
<a id="bearerAuth"></a>
#### bearerAuth


- **Type**: HTTP Bearer Token authentication (JWT)

## About

This TypeScript SDK client supports the [Fetch API](https://fetch.spec.whatwg.org/)
and is automatically generated by the
[OpenAPI Generator](https://openapi-generator.tech) project:

- API version: `2023-10-01`
- Package version: `0.0.0-private`
- Generator version: `7.22.0`
- Build package: `org.openapitools.codegen.languages.TypeScriptFetchClientCodegen`

The generated npm module supports the following:

- Environments
  * Node.js
  * Webpack
  * Browserify
- Language levels
  * ES5 - you must have a Promises/A+ library installed
  * ES6
- Module systems
  * CommonJS
  * ES6 module system

For more information, please visit [https://twitter.com/scottarbeit](https://twitter.com/scottarbeit)

## Development

### Building

To build the TypeScript source code, you need to have Node.js and npm installed.
After cloning the repository, navigate to the project directory and run:

```bash
npm install
npm run build
```

### Publishing

Once you've built the package, you can publish it to npm:

```bash
npm publish
```

## License

[MIT](https://opensource.org/licenses/MIT)
