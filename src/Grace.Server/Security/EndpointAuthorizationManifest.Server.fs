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
        | AnyOf of EndpointSecurity list
        | AllOf of EndpointSecurity list

    type EndpointSecurityDefinition = { Method: string; Path: string; Security: EndpointSecurity }

    let endpoint method_ path security = { Method = method_; Path = path; Security = security }

    let definitions: EndpointSecurityDefinition list =
        [
            endpoint "GET" "/" AllowAnonymous
            endpoint "POST" "/authorize/check-permission" Authenticated
            endpoint "POST" "/authorize/grant-role" (Authorized(SystemAdmin, System))
            endpoint "POST" "/authorize/list-path-permissions" (Authorized(RepositoryAdmin, Repository))
            endpoint "POST" "/authorize/list-role-assignments" (Authorized(SystemAdmin, System))
            endpoint "GET" "/authorize/list-roles" Authenticated
            endpoint "POST" "/authorize/remove-path-permission" (Authorized(RepositoryAdmin, Repository))
            endpoint "POST" "/authorize/revoke-role" (Authorized(SystemAdmin, System))
            endpoint "POST" "/authorize/show" Authenticated
            endpoint "POST" "/authorize/upsert-path-permission" (Authorized(RepositoryAdmin, Repository))
            endpoint "POST" "/admin/deleteAllFromCosmosDB" (Authorized(SystemAdmin, System))
            endpoint "POST" "/admin/deleteAllRemindersFromCosmosDB" (Authorized(SystemAdmin, System))
            endpoint "POST" "/approval/policy/create" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/list" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/show" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/update" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/enable" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/disable" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/delete" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/policy/evaluate" (Authorized(ApprovalPolicyManage, Repository))
            endpoint "POST" "/approval/request/list" (Authorized(ApprovalRequestRead, Repository))
            endpoint "POST" "/approval/request/show" (Authorized(ApprovalRequestRead, Repository))
            endpoint "POST" "/approval/request/approve" (Authorized(ApprovalRequestRespond, Repository))
            endpoint "POST" "/approval/request/reject" (Authorized(ApprovalRequestRespond, Repository))
            endpoint "POST" "/approval/request/history" (Authorized(ApprovalRequestRead, Repository))
            endpoint "POST" "/approval/request/_seedGenerated" Authenticated
            endpoint "POST" "/webhook/rule/create" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/list" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/show" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/update" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/enable" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/disable" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/delete" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/rule/test" (Authorized(WebhookManage, Repository))
            endpoint "POST" "/webhook/delivery/list" (Authorized(WebhookDeliveryRead, Repository))
            endpoint "POST" "/webhook/delivery/show" (Authorized(WebhookDeliveryRead, Repository))
            endpoint "GET" "/authenticate/login" AllowAnonymous
            endpoint "GET" "/authenticate/login/%s" AllowAnonymous
            endpoint "GET" "/authenticate/logout" Authenticated
            endpoint "GET" "/authenticate/me" Authenticated
            endpoint "GET" "/authenticate/oidc/config" AllowAnonymous
            endpoint "POST" "/authenticate/token/create" Authenticated
            endpoint "POST" "/authenticate/token/list" Authenticated
            endpoint "POST" "/authenticate/token/revoke" Authenticated
            endpoint "POST" "/agent/session/active" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/agent/session/listActive" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/agent/session/start" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/agent/session/status" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/agent/session/stop" (Authorized(RepositoryWrite, Repository))
            endpoint
                "POST"
                "/branch/assign"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint
                "POST"
                "/branch/annotate"
                (AllOf [ Authorized(BranchRead, Branch)
                         Authorized(PathRead, Path) ])
            endpoint
                "POST"
                "/branch/checkpoint"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint
                "POST"
                "/branch/commit"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint
                "POST"
                "/branch/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint
                "POST"
                "/branch/createExternal"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
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
            endpoint
                "POST"
                "/branch/promote"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint "POST" "/branch/rebase" Authenticated
            endpoint
                "POST"
                "/branch/save"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint "POST" "/branch/setPromotionMode" Authenticated
            endpoint
                "POST"
                "/branch/tag"
                (AnyOf [ Authorized(BranchAdmin, Branch)
                         Authorized(BranchWrite, Branch) ])
            endpoint "POST" "/branch/updateParentBranch" Authenticated
            endpoint
                "POST"
                "/promotion-set/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "POST" "/promotion-set/get" Authenticated
            endpoint "POST" "/promotion-set/get-events" Authenticated
            endpoint "POST" "/promotion-set/update-input-promotions" Authenticated
            endpoint "POST" "/promotion-set/recompute" Authenticated
            endpoint "POST" "/promotion-set/apply" Authenticated
            endpoint "POST" "/promotion-set/%O/resolve-conflicts" Authenticated
            endpoint "POST" "/promotion-set/delete" Authenticated
            endpoint
                "POST"
                "/validation-set/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "POST" "/validation-set/get" Authenticated
            endpoint "POST" "/validation-set/update" Authenticated
            endpoint "POST" "/validation-set/delete" Authenticated
            endpoint
                "POST"
                "/validation-result/record"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint
                "POST"
                "/artifact/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "GET" "/artifact/%O/download-uri" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/diff/getDiff" Authenticated
            endpoint "POST" "/diff/getDiffBySha256Hash" Authenticated
            endpoint "POST" "/diff/populate" Authenticated
            endpoint
                "POST"
                "/directory/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "POST" "/directory/get" Authenticated
            endpoint "POST" "/directory/getByDirectoryIds" Authenticated
            endpoint "POST" "/directory/getBySha256Hash" Authenticated
            endpoint "POST" "/directory/getDirectoryVersionsRecursive" Authenticated
            endpoint "POST" "/directory/getZipFile" Authenticated
            endpoint
                "POST"
                "/directory/saveDirectoryVersions"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "GET" "/healthz" AllowAnonymous
            endpoint
                "POST"
                "/organization/create"
                (AnyOf [ Authorized(OwnerAdmin, Owner)
                         Authorized(OwnerWrite, Owner) ])
            endpoint "POST" "/organization/delete" Authenticated
            endpoint "POST" "/organization/get" (Authorized(OrganizationRead, Organization))
            endpoint "POST" "/organization/listRepositories" Authenticated
            endpoint "POST" "/organization/setDescription" Authenticated
            endpoint "POST" "/organization/setName" (Authorized(OrganizationAdmin, Organization))
            endpoint "POST" "/organization/setSearchVisibility" Authenticated
            endpoint "POST" "/organization/setType" Authenticated
            endpoint "POST" "/organization/undelete" Authenticated
            endpoint
                "POST"
                "/owner/create"
                (AnyOf [ Authorized(SystemAdmin, System)
                         Authorized(SystemOperate, System) ])
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
            endpoint "POST" "/policy/_seedSnapshot" Authenticated
            endpoint "POST" "/queue/dequeue" Authenticated
            endpoint "POST" "/queue/enqueue" Authenticated
            endpoint "POST" "/queue/pause" Authenticated
            endpoint "POST" "/queue/resume" Authenticated
            endpoint "POST" "/queue/status" Authenticated
            endpoint
                "POST"
                "/reminder/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "POST" "/reminder/delete" Authenticated
            endpoint "POST" "/reminder/get" Authenticated
            endpoint "POST" "/reminder/list" Authenticated
            endpoint "POST" "/reminder/reschedule" Authenticated
            endpoint "POST" "/reminder/updateTime" Authenticated
            endpoint
                "POST"
                "/repository/create"
                (AnyOf [ Authorized(OrganizationAdmin, Organization)
                         Authorized(OrganizationWrite, Organization) ])
            endpoint "POST" "/repository/delete" Authenticated
            endpoint "POST" "/repository/exists" Authenticated
            endpoint "POST" "/repository/get" (Authorized(RepositoryRead, Repository))
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
            endpoint "POST" "/repository/setVisibility" (Authorized(RepositoryAdmin, Repository))
            endpoint "POST" "/repository/undelete" Authenticated
            endpoint "POST" "/review/checkpoint" Authenticated
            endpoint "POST" "/review/candidate/attestations" Authenticated
            endpoint "POST" "/review/candidate/cancel" Authenticated
            endpoint "POST" "/review/candidate/gate-rerun" Authenticated
            endpoint "POST" "/review/candidate/get" Authenticated
            endpoint "POST" "/review/candidate/required-actions" Authenticated
            endpoint "POST" "/review/candidate/resolve" Authenticated
            endpoint "POST" "/review/candidate/retry" Authenticated
            endpoint "POST" "/review/deepen" Authenticated
            endpoint "POST" "/review/notes" Authenticated
            endpoint "POST" "/review/report/get" Authenticated
            endpoint "POST" "/review/resolve" Authenticated
            endpoint "POST" "/storage/getDownloadUri" (Authorized(PathRead, Path))
            endpoint "POST" "/storage/claimReuseRanges" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/confirmContentBlockUpload" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/discoverContentBlocks" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/storage/finalizeManifestUpload" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/getContentBlockDownloadUri" (Authorized(PathRead, Path))
            endpoint "POST" "/storage/getContentBlockUploadUri" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/getUploadMetadataForFiles" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/getUploadUri" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/issueDedupeDiscovery" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/registerContentBlockUpload" (Authorized(PathWrite, Path))
            endpoint "POST" "/storage/startManifestUploadSession" (Authorized(PathWrite, Path))
            endpoint
                "POST"
                "/work/create"
                (AnyOf [ Authorized(RepositoryAdmin, Repository)
                         Authorized(RepositoryWrite, Repository) ])
            endpoint "POST" "/work/add-summary" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/get" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/work/link/artifact" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/link/promotion-set" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/link/reference" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/links/list" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/work/attachments/list" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/work/attachments/show" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/work/attachments/download" (Authorized(RepositoryRead, Repository))
            endpoint "POST" "/work/links/remove/artifact" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/links/remove/artifact-type" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/links/remove/promotion-set" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/links/remove/reference" (Authorized(RepositoryWrite, Repository))
            endpoint "POST" "/work/update" (Authorized(RepositoryWrite, Repository))
            endpoint "GET" "/metrics" (Authorized(SystemAdmin, System))
            endpoint "GET" "/notifications" Authenticated
        ]
