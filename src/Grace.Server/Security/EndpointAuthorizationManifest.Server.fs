namespace Grace.Server.Security

open Grace.Types.Authorization

module EndpointAuthorizationManifest =

    type ResourceKind =
        | System
        | Owner
        | Organization
        | Repository
        | Branch
        | Path

    type EndpointSecurity =
        | AllowAnonymous
        | Authenticated
        | Authorized of Operation * ResourceKind

    type EndpointSecurityDefinition =
        {
            Method: string
            Path: string
            Security: EndpointSecurity
        }

    let endpoint method_ path security =
        {
            Method = method_
            Path = path
            Security = security
        }

    let definitions: EndpointSecurityDefinition list =
        [
                endpoint "GET" "/" AllowAnonymous
                endpoint "POST" "/access/checkPermission" Authenticated
                endpoint "POST" "/access/grantRole" (Authorized(SystemAdmin, System))
                endpoint "POST" "/access/listPathPermissions" (Authorized(RepoAdmin, Repository))
                endpoint "POST" "/access/listRoleAssignments" (Authorized(SystemAdmin, System))
                endpoint "GET" "/access/listRoles" Authenticated
                endpoint "POST" "/access/removePathPermission" (Authorized(RepoAdmin, Repository))
                endpoint "POST" "/access/revokeRole" (Authorized(SystemAdmin, System))
                endpoint "POST" "/access/upsertPathPermission" (Authorized(RepoAdmin, Repository))
                endpoint "POST" "/admin/deleteAllFromCosmosDB" (Authorized(SystemAdmin, System))
                endpoint "POST" "/admin/deleteAllRemindersFromCosmosDB" (Authorized(SystemAdmin, System))
                endpoint "GET" "/auth/login" AllowAnonymous
                endpoint "GET" "/auth/login/%s" AllowAnonymous
                endpoint "GET" "/auth/logout" Authenticated
                endpoint "GET" "/auth/me" Authenticated
                endpoint "GET" "/auth/oidc/config" AllowAnonymous
                endpoint "POST" "/auth/token/create" Authenticated
                endpoint "POST" "/auth/token/list" Authenticated
                endpoint "POST" "/auth/token/revoke" Authenticated
                endpoint "POST" "/branch/assign" Authenticated
                endpoint "POST" "/branch/checkpoint" Authenticated
                endpoint "POST" "/branch/commit" (Authorized(BranchWrite, Branch))
                endpoint "POST" "/branch/create" Authenticated
                endpoint "POST" "/branch/createExternal" Authenticated
                endpoint "POST" "/branch/delete" Authenticated
                endpoint "POST" "/branch/enableAssign" Authenticated
                endpoint "POST" "/branch/enableAutoRebase" Authenticated
                endpoint "POST" "/branch/enableCheckpoint" Authenticated
                endpoint "POST" "/branch/enableCommit" (Authorized(BranchAdmin, Branch))
                endpoint "POST" "/branch/enableExternal" Authenticated
                endpoint "POST" "/branch/enablePromotion" Authenticated
                endpoint "POST" "/branch/enableSave" Authenticated
                endpoint "POST" "/branch/enableTag" Authenticated
                endpoint "POST" "/branch/get" (Authorized(BranchRead, Branch))
                endpoint "POST" "/branch/getCheckpoints" Authenticated
                endpoint "POST" "/branch/getCommits" Authenticated
                endpoint "POST" "/branch/getDiffsForReferenceType" Authenticated
                endpoint "POST" "/branch/getEvents" Authenticated
                endpoint "POST" "/branch/getExternals" Authenticated
                endpoint "POST" "/branch/getParentBranch" Authenticated
                endpoint "POST" "/branch/getPromotions" Authenticated
                endpoint "POST" "/branch/getRecursiveSize" Authenticated
                endpoint "POST" "/branch/getReference" Authenticated
                endpoint "POST" "/branch/getReferences" Authenticated
                endpoint "POST" "/branch/getSaves" Authenticated
                endpoint "POST" "/branch/getTags" Authenticated
                endpoint "POST" "/branch/getVersion" Authenticated
                endpoint "POST" "/branch/listContents" Authenticated
                endpoint "POST" "/branch/promote" Authenticated
                endpoint "POST" "/branch/rebase" Authenticated
                endpoint "POST" "/branch/save" Authenticated
                endpoint "POST" "/branch/setPromotionMode" Authenticated
                endpoint "POST" "/branch/tag" Authenticated
                endpoint "POST" "/branch/updateParentBranch" Authenticated
                endpoint "POST" "/candidate/attestations" Authenticated
                endpoint "POST" "/candidate/cancel" Authenticated
                endpoint "POST" "/candidate/gate/rerun" Authenticated
                endpoint "POST" "/candidate/get" Authenticated
                endpoint "POST" "/candidate/required-actions" Authenticated
                endpoint "POST" "/candidate/retry" Authenticated
                endpoint "POST" "/diff/getDiff" Authenticated
                endpoint "POST" "/diff/getDiffBySha256Hash" Authenticated
                endpoint "POST" "/diff/populate" Authenticated
                endpoint "POST" "/directory/create" Authenticated
                endpoint "POST" "/directory/get" Authenticated
                endpoint "POST" "/directory/getByDirectoryIds" Authenticated
                endpoint "POST" "/directory/getBySha256Hash" Authenticated
                endpoint "POST" "/directory/getDirectoryVersionsRecursive" Authenticated
                endpoint "POST" "/directory/getZipFile" Authenticated
                endpoint "POST" "/directory/saveDirectoryVersions" Authenticated
                endpoint "GET" "/healthz" AllowAnonymous
                endpoint "POST" "/organization/create" Authenticated
                endpoint "POST" "/organization/delete" Authenticated
                endpoint "POST" "/organization/get" (Authorized(OrgRead, Organization))
                endpoint "POST" "/organization/listRepositories" Authenticated
                endpoint "POST" "/organization/setDescription" Authenticated
                endpoint "POST" "/organization/setName" (Authorized(OrgAdmin, Organization))
                endpoint "POST" "/organization/setSearchVisibility" Authenticated
                endpoint "POST" "/organization/setType" Authenticated
                endpoint "POST" "/organization/undelete" Authenticated
                endpoint "POST" "/owner/create" Authenticated
                endpoint "POST" "/owner/delete" Authenticated
                endpoint "POST" "/owner/get" (Authorized(OwnerRead, Owner))
                endpoint "POST" "/owner/listOrganizations" Authenticated
                endpoint "POST" "/owner/setDescription" Authenticated
                endpoint "POST" "/owner/setName" (Authorized(OwnerAdmin, Owner))
                endpoint "POST" "/owner/setSearchVisibility" Authenticated
                endpoint "POST" "/owner/setType" Authenticated
                endpoint "POST" "/owner/undelete" Authenticated
                endpoint "POST" "/policy/acknowledge" Authenticated
                endpoint "POST" "/policy/current" Authenticated
                endpoint "POST" "/promotionGroup/addPromotion" Authenticated
                endpoint "POST" "/promotionGroup/block" Authenticated
                endpoint "POST" "/promotionGroup/complete" Authenticated
                endpoint "POST" "/promotionGroup/create" Authenticated
                endpoint "POST" "/promotionGroup/delete" Authenticated
                endpoint "POST" "/promotionGroup/get" Authenticated
                endpoint "POST" "/promotionGroup/getEvents" Authenticated
                endpoint "POST" "/promotionGroup/markReady" Authenticated
                endpoint "POST" "/promotionGroup/removePromotion" Authenticated
                endpoint "POST" "/promotionGroup/reorderPromotions" Authenticated
                endpoint "POST" "/promotionGroup/schedule" Authenticated
                endpoint "POST" "/promotionGroup/start" Authenticated
                endpoint "POST" "/queue/dequeue" Authenticated
                endpoint "POST" "/queue/enqueue" Authenticated
                endpoint "POST" "/queue/pause" Authenticated
                endpoint "POST" "/queue/resume" Authenticated
                endpoint "POST" "/queue/status" Authenticated
                endpoint "POST" "/reminder/create" Authenticated
                endpoint "POST" "/reminder/delete" Authenticated
                endpoint "POST" "/reminder/get" Authenticated
                endpoint "POST" "/reminder/list" Authenticated
                endpoint "POST" "/reminder/reschedule" Authenticated
                endpoint "POST" "/reminder/updateTime" Authenticated
                endpoint "POST" "/repository/create" Authenticated
                endpoint "POST" "/repository/delete" Authenticated
                endpoint "POST" "/repository/exists" Authenticated
                endpoint "POST" "/repository/get" (Authorized(RepoRead, Repository))
                endpoint "POST" "/repository/getBranches" Authenticated
                endpoint "POST" "/repository/getBranchesByBranchId" Authenticated
                endpoint "POST" "/repository/getReferencesByReferenceId" Authenticated
                endpoint "POST" "/repository/isEmpty" Authenticated
                endpoint "POST" "/repository/setAllowsLargeFiles" Authenticated
                endpoint "POST" "/repository/setAnonymousAccess" Authenticated
                endpoint "POST" "/repository/setCheckpointDays" Authenticated
                endpoint "POST" "/repository/setConflictResolutionPolicy" Authenticated
                endpoint "POST" "/repository/setDefaultServerApiVersion" Authenticated
                endpoint "POST" "/repository/setDescription" Authenticated
                endpoint "POST" "/repository/setDiffCacheDays" Authenticated
                endpoint "POST" "/repository/setDirectoryVersionCacheDays" Authenticated
                endpoint "POST" "/repository/setLogicalDeleteDays" Authenticated
                endpoint "POST" "/repository/setName" Authenticated
                endpoint "POST" "/repository/setRecordSaves" Authenticated
                endpoint "POST" "/repository/setSaveDays" Authenticated
                endpoint "POST" "/repository/setStatus" Authenticated
                endpoint "POST" "/repository/setVisibility" (Authorized(RepoAdmin, Repository))
                endpoint "POST" "/repository/undelete" Authenticated
                endpoint "POST" "/review/checkpoint" Authenticated
                endpoint "POST" "/review/deepen" Authenticated
                endpoint "POST" "/review/packet" Authenticated
                endpoint "POST" "/review/resolve" Authenticated
                endpoint "POST" "/storage/getDownloadUri" (Authorized(PathRead, Path))
                endpoint "POST" "/storage/getUploadMetadataForFiles" (Authorized(PathWrite, Path))
                endpoint "POST" "/storage/getUploadUri" (Authorized(PathWrite, Path))
                endpoint "POST" "/work/create" (Authorized(RepoWrite, Repository))
                endpoint "POST" "/work/get" Authenticated
                endpoint "POST" "/work/link/promotion-group" Authenticated
                endpoint "POST" "/work/link/reference" Authenticated
                endpoint "POST" "/work/update" Authenticated
                endpoint "GET" "/metrics" (Authorized(SystemAdmin, System))
                endpoint "GET" "/notifications" Authenticated
        ]
