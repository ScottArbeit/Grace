using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grace.SDK;
using static Grace.Shared.Dto.Diff;
using static Grace.Shared.Dto.Reference;
using static Grace.Shared.Parameters.Branch;
using static Grace.Shared.Parameters.Diff;
using static Grace.Shared.Types;
using static Grace.Shared.Utilities;

namespace GraceApp
{
    internal class Services
    {
        const string ownerId = "ce93e5ee-d8e1-414f-98b8-39ba1b04a54b";
        const string organizationId = "b4ba1b0d-8036-40e8-a4c0-12a85f5ba3f3";
        const string repositoryId = "120bb6f2-5302-47b4-9499-abbeb7d0858b";
        //const string branchId = "713e833e-a60f-4965-9ea9-eeedc75162a2";
        const string branchId = "0662b79a-ae83-4fbc-b696-df2e1c0532c2";
        const string parentBranchId = "5a143ffb-772d-4680-b80d-fa2cdfda01ec";

        public static void LogToConsole(string message)
        {
            Debug.WriteLine($"{getCurrentInstantExtended()}: {message}");
        }

        public static async Task<IEnumerable<ReferenceDto>> GetReferences()
        {
            var getReferencesParameters = new GetReferencesParameters() { OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryId, BranchId = branchId };
            var getReferencesResult = await Branch.GetReferences(getReferencesParameters);
            if (getReferencesResult.IsOk)
            {
                IEnumerable<ReferenceDto> refs = getReferencesResult.ResultValue.ReturnValue;
                Console.WriteLine("Retrieved results.");
                return refs;
            }
            else
            {
                var returnValue = new List<ReferenceDto>();
                return returnValue;
            }
        }

        public static async Task<Tuple<IReadOnlyList<ReferenceDto>, IReadOnlyList<DiffDto>>> GetDiffsForReferenceType(ReferenceType referenceType)
        {
            var getDiffsForReferenceTypeParameters = new GetDiffsForReferenceTypeParameters() 
                { OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryId, BranchId = branchId, ReferenceType = discriminatedUnionCaseNameToString(referenceType) };
            var getDiffsResult = await Branch.GetDiffsForReferenceType(getDiffsForReferenceTypeParameters);
            if (getDiffsResult.IsOk)
            {
                var (references, diffs) = getDiffsResult.ResultValue.ReturnValue;
                Console.WriteLine("Retrieved results.");
                return new Tuple<IReadOnlyList<ReferenceDto>, IReadOnlyList<DiffDto>>(references, diffs);
            }
            else
            {
                var returnValue = new List<DiffDto>();
                return new Tuple<IReadOnlyList<ReferenceDto>, IReadOnlyList<DiffDto>>(new List<ReferenceDto>(), new List<DiffDto>());
            }
        }

        public static async Task<IEnumerable<DiffDto>> GetDiffs(IEnumerable<ReferenceDto> refs)
        {
            List<DiffDto> diffs = new();

            if (refs.Count() > 1)
            {
                var sortedRefs = refs.OrderByDescending(referenceDto => referenceDto.CreatedAt).ToList();
                for (int i = 0; i < sortedRefs.Count() - 1; i++)
                {
                    var diffParameters = new GetDiffParameters() { DirectoryId1 = sortedRefs[i].DirectoryId, DirectoryId2 = sortedRefs[i + 1].DirectoryId };
                    var diffResult = await Diff.GetDiff(diffParameters);
                    Console.WriteLine("Retrieved results.");
                    if (diffResult.IsOk)
                    {
                        DiffDto diff = diffResult.ResultValue.ReturnValue;
                        diffs.Add(diff);
                    }
                }
            }

            return diffs;
        }

    }
}
