﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Google;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Requests;
using Newtonsoft.Json;
using Lithnet.GoogleApps.ManagedObjects;

namespace Lithnet.GoogleApps
{
    public static class GroupMemberRequestFactory
    {
        internal static string ServiceName = "GroupMemberRequestFactory";

        // Batch size can technically be up to 1000, but a google API error is returned with requests that seem to take more than 5.5 minutes
        // 500 works, but can timeout on the client side, unless the default HttpClient timeout is raised from the default of 100 seconds
        // @ 250 we seem to get comparible updated objects/sec as at batch sizes of 500 and 750.
        public static int BatchSize { get; set; } = 100;

        public static GroupMembership GetMembership(string groupKey)
        {
            GroupMembership membership = new GroupMembership();

            using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
            {
                string token = null;
                MembersResource.ListRequest request = poolService.Item.Members.List(groupKey);
                request.PrettyPrint = false;
                Trace.WriteLine($"Getting members from group {groupKey}");
                
                do
                {
                    request.PageToken = token;
                    Members members;

                    try
                    {
                        GroupMemberRequestFactory.WaitForGate();
                        members = request.ExecuteWithBackoff();
                    }
                    finally
                    {
                        GroupMemberRequestFactory.ReleaseGate();
                    }

                    if (members.MembersValue != null)
                    {
                        foreach (Member member in members.MembersValue)
                        {
                            if (!string.IsNullOrWhiteSpace(member.Email))
                            {
                                membership.AddMember(member);
                            }
                        }
                    }

                    token = members.NextPageToken;
                } while (token != null);
            }

            Trace.WriteLine($"Returned {membership.Count} members from group {groupKey}");

            return membership;
        }

        private static void WaitForGate()
        {
            RateLimiter.WaitForGate(GroupMemberRequestFactory.ServiceName);
        }

        private static void ReleaseGate()
        {
            RateLimiter.ReleaseGate(GroupMemberRequestFactory.ServiceName);
        }

        public static void AddMember(string groupID, string memberID, string role)
        {
            Member member = new Member();

            if (memberID.IndexOf('@') < 0)
            {
                member.Id = memberID;
            }
            else
            {
                member.Email = memberID;
            }

            member.Role = role ?? "MEMBER";

            AddMember(groupID, member);
        }

        public static void AddMember(string groupID, Member item)
        {
            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    MembersResource.InsertRequest request = poolService.Item.Members.Insert(item, groupID);
                    Trace.WriteLine($"Adding member {item.Email ?? item.Id} as {item.Role} to group {groupID}");
                    request.ExecuteWithBackoff();
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }

        public static void ChangeMemberRole(string groupID, Member item)
        {
            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    MembersResource.PatchRequest request = poolService.Item.Members.Patch(item, groupID, item.Email);
                    Trace.WriteLine($"Changing member {item.Email ?? item.Id} role to {item.Role} in group {groupID}");
                    request.ExecuteWithBackoff();
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }

        public static void RemoveMember(string groupID, string memberID)
        {
            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    MembersResource.DeleteRequest request = poolService.Item.Members.Delete(groupID, memberID);
                    Trace.WriteLine($"Removing member {memberID} from group {groupID}");
                    request.ExecuteWithBackoff();
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }

        public static void AddMembers(string id, IList<Member> members, bool throwOnExistingMember)
        {
            if (GroupMemberRequestFactory.BatchSize <= 1)
            {
                foreach (Member member in members)
                {
                    GroupMemberRequestFactory.AddMember(id, member);
                }

                return;
            }

            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    List<ClientServiceRequest<Member>> requests = new List<ClientServiceRequest<Member>>();

                    foreach (Member member in members)
                    {
                        Trace.WriteLine($"Queuing batch member add for {member.Email ?? member.Id} as {member.Role} to group {id}");
                        requests.Add(poolService.Item.Members.Insert(member, id));
                    }

                    GroupMemberRequestFactory.ProcessBatches(id, members, !throwOnExistingMember, false, requests, poolService);
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }

        private static void ProcessBatches<T>(string id, IList<T> members, bool ignoreExistingMember, bool ignoreMissingMember, IList<ClientServiceRequest<T>> requests, PoolItem<DirectoryService> poolService)
        {
            List<string> failedMembers = new List<string>();
            List<Exception> failures = new List<Exception>();
            Dictionary<string, ClientServiceRequest<T>> requestsToRetry = new Dictionary<string, ClientServiceRequest<T>>();

            int baseCount = 0;
            int batchCount = 0;

            foreach (IEnumerable<ClientServiceRequest<T>> batch in requests.Batch(GroupMemberRequestFactory.BatchSize))
            {
                BatchRequest batchRequest = new BatchRequest(poolService.Item);
                Trace.WriteLine($"Executing batch {++batchCount} for group {id}");

                foreach (ClientServiceRequest<T> request in batch)
                {
                    batchRequest.Queue<MembersResource>(request,
                        (content, error, i, message) =>
                        {
                            int index = baseCount + i;
                            GroupMemberRequestFactory.ProcessMemberResponse(id, members[index], ignoreExistingMember, ignoreMissingMember, error, message, requestsToRetry, request, failedMembers, failures);
                        });
                }

                batchRequest.ExecuteWithBackoff(poolService.Item.Name);

                baseCount += GroupMemberRequestFactory.BatchSize;
            }

            if (requestsToRetry.Count > 0)
            {
                Trace.WriteLine($"Retrying {requestsToRetry} member change requests");
            }

            foreach (KeyValuePair<string, ClientServiceRequest<T>> request in requestsToRetry)
            {
                try
                {
                    request.Value.ExecuteWithBackoff();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    failedMembers.Add(request.Key);
                }
            }

            if (failures.Count == 1)
            {
                throw failures[0];
            }
            else if (failures.Count > 1)
            {
                throw new AggregateGroupUpdateException(id, failedMembers, failures);
            }
        }

        private static void ProcessMemberResponse<T>(string id, T item, bool ignoreExistingMember, bool ignoreMissingMember, RequestError error, HttpResponseMessage message, Dictionary<string, ClientServiceRequest<T>> requestsToRetry, ClientServiceRequest<T> request, List<string> failedMembers, List<Exception> failures)
        {
            string memberKey;
            string memberRole = string.Empty;

            Member member = item as Member;
            if (member == null)
            {
                memberKey = item as string ?? "unknown";
            }
            else
            {
                memberKey = member.Email ?? member.Id;
                memberRole = member.Role;
            }

            string requestType = request.GetType().Name;

            if (error == null)
            {
                Trace.WriteLine($"{requestType}: Success: Member: {memberKey}, Role: {memberRole}, Group: {id}");
                return;
            }

            string errorString = $"{error}\nFailed {requestType}: {memberKey}\nGroup: {id}";

            Trace.WriteLine($"{requestType}: Failed: Member: {memberKey}, Role: {memberRole}, Group: {id}\n{error}");

            if (ignoreExistingMember)
            {
                if (message.StatusCode == HttpStatusCode.Conflict && errorString.IndexOf("member already exists", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }
            }

            if (ignoreMissingMember)
            {
                if (message.StatusCode == HttpStatusCode.NotFound && errorString.IndexOf("Resource Not Found: memberKey", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }
            }

            if (message.StatusCode == HttpStatusCode.Forbidden && errorString.IndexOf("quotaExceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Trace.WriteLine($"Queuing {requestType} of {memberKey} for backoff/retry");
                requestsToRetry.Add(memberKey, request);
                return;
            }

            if (message.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                Trace.WriteLine($"Queuing {requestType} of {memberKey} for backoff/retry");
                requestsToRetry.Add(memberKey, request);
                return;
            }

            GoogleApiException ex = new GoogleApiException("admin", errorString);
            ex.HttpStatusCode = message.StatusCode;
            failedMembers.Add(memberKey);
            failures.Add(ex);
        }

        public static void RemoveMembers(string id, IList<string> members, bool throwOnMissingMember)
        {
            if (GroupMemberRequestFactory.BatchSize <= 1)
            {
                foreach (string member in members)
                {
                    GroupMemberRequestFactory.RemoveMember(id, member);
                }

                return;
            }

            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    List<ClientServiceRequest<string>> requests = new List<ClientServiceRequest<string>>();

                    foreach (string member in members)
                    {
                        Trace.WriteLine($"Queuing batch member delete for {member} from group {id}");
                        requests.Add(poolService.Item.Members.Delete(id, member));
                    }

                    GroupMemberRequestFactory.ProcessBatches(id, members, false, !throwOnMissingMember, requests, poolService);
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }

        public static void ChangeMemberRoles(string id, IList<Member> members)
        {
            if (GroupMemberRequestFactory.BatchSize <= 1)
            {
                foreach (Member member in members)
                {
                    GroupMemberRequestFactory.ChangeMemberRole(id, member);
                }

                return;
            }

            try
            {
                GroupMemberRequestFactory.WaitForGate();

                using (PoolItem<DirectoryService> poolService = ConnectionPools.DirectoryServicePool.Take(NullValueHandling.Ignore))
                {
                    List<ClientServiceRequest<Member>> requests = new List<ClientServiceRequest<Member>>();

                    foreach (Member member in members)
                    {
                        string memberKey = member.Email ?? member.Id;

                        Trace.WriteLine($"Queuing batch member role change for {memberKey} as {member.Role} to group {id}");
                        requests.Add(poolService.Item.Members.Patch(member, id, memberKey));
                    }

                    GroupMemberRequestFactory.ProcessBatches(id, members, false, false, requests, poolService);
                }
            }
            finally
            {
                GroupMemberRequestFactory.ReleaseGate();
            }
        }
    }
}