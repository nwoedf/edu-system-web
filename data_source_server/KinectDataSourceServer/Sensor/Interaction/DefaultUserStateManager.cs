﻿using System;
using System.Collections.Generic;
using System.Linq;
using DataSourceServer.Message.Event;
using Microsoft.Kinect;
using Microsoft.Kinect.Toolkit;
using Microsoft.Kinect.Toolkit.Interaction;

namespace KinectDataSourceServer.Sensor.Interaction
{
    public class DefaultUserStateManager : IUserStateManager
    {
        public const string TrackedStateName = "tracked";
        public const string EngagedStateName = "engaged";
        public const string EventCategory = "userState";
        public const string PrimaryUserChangedEventType = "primaryUserChanged";
        public const string UserStatesChangedEventType = "userStatesChanged";

        internal const long MinimumInactivityBeforeTrackingMilliseconds = 500;
        private readonly UserActivityMeter activityMeter;

        private HashSet<int> previousTrackedUserTrackingIds;
        private Dictionary<int, string> publicUserStates;
        private Dictionary<int, string> userStatesAccumulator;

        private readonly object lockObject = new object();
        public event EventHandler<UserStateChangedEventArgs> UserStateChanged;

        public IDictionary<int, string> UserStates
        {
            get { return this.publicUserStates; }
        }

        public int PrimaryUserTrackingId { get; set; }

        private int EngagedUserTrackingId { get; set; }

        private HashSet<int> TrackedUserTrackingIds { get; set; }

        public DefaultUserStateManager()
        {
            this.activityMeter = new UserActivityMeter();
            this.previousTrackedUserTrackingIds = new HashSet<int>();
            this.publicUserStates = new Dictionary<int, string>();
            this.userStatesAccumulator = new Dictionary<int, string>();
            this.TrackedUserTrackingIds = new HashSet<int>();
        }

        public void ChooseTrackedUsers(Microsoft.Kinect.Skeleton[] frameSkeletons, long timestamp, int[] chosenTrackingIds)
        {
            if (frameSkeletons == null)
            {
                throw new ArgumentNullException("frameSkeletons");
            }

            if (chosenTrackingIds == null)
            {
                throw new ArgumentNullException("chosenTrackingIds");
            }

            var availableSkeletons = new List<Skeleton>(
                from skeleton in frameSkeletons
                where
                    (skeleton.TrackingId != SharedConstants.InvalidUserTrackingId)
                    &&
                    ((skeleton.TrackingState == SkeletonTrackingState.Tracked)
                     || (skeleton.TrackingState == SkeletonTrackingState.PositionOnly))
                select skeleton);
            var trackingCandidateSkeletons = new List<Skeleton>();

            this.activityMeter.Update(availableSkeletons, timestamp);

            foreach (var skeleton in availableSkeletons)
            {
                UserActivityRecord record;
                if (this.activityMeter.TryGetActivityRecord(skeleton.TrackingId, out record))
                {
                    // The tracked skeletons become candidate skeletons for tracking if we have an activity record for them.
                    trackingCandidateSkeletons.Add(skeleton);
                }
            }

            // sort the currently tracked skeletons according to our tracking choice criteria
            trackingCandidateSkeletons.Sort((left, right) => this.ComputeTrackingMetric(right).CompareTo(this.ComputeTrackingMetric(left)));

            for (int i = 0; i < chosenTrackingIds.Length; ++i)
            {
                chosenTrackingIds[i] = (i < trackingCandidateSkeletons.Count) ? trackingCandidateSkeletons[i].TrackingId : SharedConstants.InvalidUserTrackingId;
            }
        }

        public void UpdateUserInformation(IEnumerable<UserInfo> trackedUserInfo, long timestamp)
        {
            bool foundEngagedUser = false;
            int firstTrackedUser = SharedConstants.InvalidUserTrackingId;

            using (var callbackLock = new CallbackLock(this.lockObject))
            {
                this.previousTrackedUserTrackingIds.Clear();
                var nextTrackedIds = this.previousTrackedUserTrackingIds;
                this.previousTrackedUserTrackingIds = this.TrackedUserTrackingIds;
                this.TrackedUserTrackingIds = nextTrackedIds;

                var trackedUserInfoArray = trackedUserInfo as UserInfo[] ?? trackedUserInfo.ToArray();

                foreach (var userInfo in trackedUserInfoArray)
                {
                    if (userInfo.SkeletonTrackingId == SharedConstants.InvalidUserTrackingId)
                    {
                        continue;
                    }

                    if (this.EngagedUserTrackingId == userInfo.SkeletonTrackingId)
                    {
                        this.TrackedUserTrackingIds.Add(userInfo.SkeletonTrackingId);

                        foundEngagedUser = true;
                    }
                    else if (HasTrackedHands(userInfo)
                             && (this.previousTrackedUserTrackingIds.Contains(userInfo.SkeletonTrackingId)
                                 || this.IsInactive(userInfo, timestamp)))
                    {
                        // Keep track of the non-engaged users we find that have at least one
                        // tracked hand pointer and also either (1) were previously tracked or
                        // (2) are not moving too much
                        this.TrackedUserTrackingIds.Add(userInfo.SkeletonTrackingId);

                        if (firstTrackedUser == SharedConstants.InvalidUserTrackingId)
                        {
                            // Consider the first non-engaged, stationary user as a candidate for engagement
                            firstTrackedUser = userInfo.SkeletonTrackingId;
                        }
                    }
                }

                // If engaged user was not found in list of candidate users, engaged user has become invalid.
                if (!foundEngagedUser)
                {
                    this.EngagedUserTrackingId = SharedConstants.InvalidUserTrackingId;
                }

                // Decide who should be the primary user, if anyone
                this.UpdatePrimaryUser(trackedUserInfoArray, callbackLock);

                // If there's a primary user, it is the preferred candidate for engagement.
                // Otherwise, the first tracked user seen is the preferred candidate.
                int candidateUserTrackingId = (this.PrimaryUserTrackingId != SharedConstants.InvalidUserTrackingId)
                                                  ? this.PrimaryUserTrackingId
                                                  : firstTrackedUser;

                // If there is a valid candidate user that is not already the engaged user
                if ((candidateUserTrackingId != SharedConstants.InvalidUserTrackingId)
                    && (candidateUserTrackingId != this.EngagedUserTrackingId))
                {
                    // If there is currently no engaged user, or if candidate user is the
                    // primary user controlling interactions while the currently engaged user
                    // is not interacting
                    if ((this.EngagedUserTrackingId == SharedConstants.InvalidUserTrackingId)
                        || (candidateUserTrackingId == this.PrimaryUserTrackingId))
                    {
                        this.PromoteCandidateToEngaged(candidateUserTrackingId);
                    }
                }

                // Update user states as the very last action, to include results from updates
                // performed so far
                this.UpdateUserStates(callbackLock);
            }
        }

        public bool PromoteCandidateToEngaged(int candidateTrackingId)
        {
            bool isConfirmed = false;

            if ((candidateTrackingId != SharedConstants.InvalidUserTrackingId) && this.TrackedUserTrackingIds.Contains(candidateTrackingId))
            {
                using (var callbackLock = new CallbackLock(this.lockObject))
                {
                    this.EngagedUserTrackingId = candidateTrackingId;
                    this.UpdateUserStates(callbackLock);
                }

                isConfirmed = true;
            }

            return isConfirmed;
        }

        public SkeletonPoint? TryGetLastPositionForId(int trackingId)
        {
            if (SharedConstants.InvalidUserTrackingId == trackingId)
            {
                return null;
            }

            UserActivityRecord record;
            if (this.activityMeter.TryGetActivityRecord(trackingId, out record))
            {
                return record.LastPosition;
            }

            return null;
        }

        internal static StateMappingEntry[] GetStateMappingEntryArray(IDictionary<int, string> userStates)
        {
            var mappingEntries = new StateMappingEntry[userStates.Count];
            int entryIndex = 0;
            foreach (var userStateEntry in userStates)
            {
                mappingEntries[entryIndex] = new StateMappingEntry { id = userStateEntry.Key, userState = userStateEntry.Value };
                ++entryIndex;
            }

            return mappingEntries;
        }

        internal void SetPrimaryUserTrackingId(int newId, CallbackLock callbackLock)
        {
            int oldId = this.PrimaryUserTrackingId;
            this.PrimaryUserTrackingId = newId;

            if (oldId != newId)
            {
                callbackLock.LockExit +=
                    () =>
                    this.SendUserStateChanged(
                        new UserTrackingIdChangedEventMessage
                        {
                            category = EventCategory,
                            eventType = PrimaryUserChangedEventType,
                            oldValue = oldId,
                            newValue = newId
                        });
            }
        }

        private static bool HasTrackedHands(UserInfo userInfo)
        {
            return userInfo.HandPointers.Any(handPointer => handPointer.IsTracked);
        }

        private void UpdatePrimaryUser(IEnumerable<UserInfo> candidateUserInfo, CallbackLock callbackLock)
        {
            int firstPrimaryUserCandidate = SharedConstants.InvalidUserTrackingId;
            bool currentPrimaryUserStillPrimary = false;
            bool engagedUserIsPrimary = false;

            var trackingIdsAvailable = new HashSet<int>();

            foreach (var userInfo in candidateUserInfo)
            {
                if (userInfo.SkeletonTrackingId == SharedConstants.InvalidUserTrackingId)
                {
                    continue;
                }

                trackingIdsAvailable.Add(userInfo.SkeletonTrackingId);

                foreach (var handPointer in userInfo.HandPointers)
                {
                    if (handPointer.IsPrimaryForUser)
                    {
                        if (this.PrimaryUserTrackingId == userInfo.SkeletonTrackingId)
                        {
                            // If the current primary user still has an active hand, we should continue to consider them the primary user.
                            currentPrimaryUserStillPrimary = true;
                        }
                        else if (SharedConstants.InvalidUserTrackingId == firstPrimaryUserCandidate)
                        {
                            // Else if this is the first user with an active hand, they are the alternative candidate for primary user.
                            firstPrimaryUserCandidate = userInfo.SkeletonTrackingId;
                        }

                        if (this.EngagedUserTrackingId == userInfo.SkeletonTrackingId)
                        {
                            engagedUserIsPrimary = true;
                        }
                    }
                }
            }


            // If engaged user has a primary hand, always pick that user as primary user.
            // If current primary user still has a primary hand, let them remain primary.
            // Otherwise default to first primary user candidate seen.
            int primaryUserTrackingId = engagedUserIsPrimary
                                            ? this.EngagedUserTrackingId
                                            : (currentPrimaryUserStillPrimary ? this.PrimaryUserTrackingId : firstPrimaryUserCandidate);
            this.SetPrimaryUserTrackingId(primaryUserTrackingId, callbackLock);
        }

        private double ComputeTrackingMetric(Skeleton skeleton)
        {
            const double MaxCameraDistance = 4.0;

            // Give preference to engaged users, then to tracked users, then to users
            // near the center of the Kinect Sensor's field of view that are also
            // closer (distance) to the KinectSensor and not moving around too much.
            const double EngagedWeight = 100.0;
            const double TrackedWeight = 50.0;
            const double AngleFromCenterWeight = 1.30;
            const double DistanceFromCameraWeight = 1.15;
            const double BodyMovementWeight = 0.05;

            double engagedMetric = (skeleton.TrackingId == this.EngagedUserTrackingId) ? 1.0 : 0.0;
            double trackedMetric = this.TrackedUserTrackingIds.Contains(skeleton.TrackingId) ? 1.0 : 0.0;
            double angleFromCenterMetric = (skeleton.Position.Z > 0.0) ? (1.0 - Math.Abs(2 * Math.Atan(skeleton.Position.X / skeleton.Position.Z) / Math.PI)) : 0.0;
            double distanceFromCameraMetric = (MaxCameraDistance - skeleton.Position.Z) / MaxCameraDistance;
            UserActivityRecord activityRecord;
            double bodyMovementMetric = this.activityMeter.TryGetActivityRecord(skeleton.TrackingId, out activityRecord)
                                            ? 1.0 - activityRecord.ActivityLevel
                                            : 0.0;
            return (EngagedWeight * engagedMetric) +
                (TrackedWeight * trackedMetric) +
                (AngleFromCenterWeight * angleFromCenterMetric) +
                (DistanceFromCameraWeight * distanceFromCameraMetric) +
                (BodyMovementWeight * bodyMovementMetric);
        }

        private bool IsInactive(UserInfo userInfo, long timestamp)
        {
            UserActivityRecord record;
            return this.activityMeter.TryGetActivityRecord(userInfo.SkeletonTrackingId, out record) && !record.IsActive
                   && (record.StateTransitionTimestamp + MinimumInactivityBeforeTrackingMilliseconds <= timestamp);
        }

        private bool HaveUserStatesChanged()
        {
            if (this.publicUserStates.Count != this.userStatesAccumulator.Count)
            {
                return true;
            }

            foreach (var stateEntry in this.publicUserStates)
            {
                string accumulatorState;
                if (!this.userStatesAccumulator.TryGetValue(stateEntry.Key, out accumulatorState))
                {
                    // Key is absent from accumulator but present in current state map
                    return true;
                }

                if (!stateEntry.Value.Equals(accumulatorState))
                {
                    // state names are present in both maps, but they're different
                    return true;
                }
            }

            return false;
        }

        private void UpdateUserStates(CallbackLock callbackLock)
        {
            this.userStatesAccumulator.Clear();

            // Add states for tracked users
            foreach (var trackingId in this.TrackedUserTrackingIds)
            {
                this.userStatesAccumulator.Add(trackingId, TrackedStateName);
            }

            if (this.EngagedUserTrackingId != SharedConstants.InvalidUserTrackingId)
            {
                // Engaged state supersedes all other states
                this.userStatesAccumulator[this.EngagedUserTrackingId] = EngagedStateName;
            }

            if (this.HaveUserStatesChanged())
            {
                var temporaryMap = this.publicUserStates;
                this.publicUserStates = this.userStatesAccumulator;
                this.userStatesAccumulator = temporaryMap;

                var userStatesToSend = GetStateMappingEntryArray(this.publicUserStates);

                callbackLock.LockExit +=
                    () =>
                    this.SendUserStateChanged(
                        new UserStatesChangedEventMessage
                        {
                            category = EventCategory,
                            eventType = UserStatesChangedEventType,
                            userStates = userStatesToSend
                        });
            }
        }

        private void SendUserStateChanged(EventMessage message)
        {
            if (this.UserStateChanged != null)
            {
                this.UserStateChanged(this, new UserStateChangedEventArgs(message));
            }
        }

        public void Reset()
        {
            using (var callbackLock = new CallbackLock(this.lockObject))
            {
                this.activityMeter.Clear();
                this.TrackedUserTrackingIds.Clear();
                this.EngagedUserTrackingId = SharedConstants.InvalidUserTrackingId;
                this.SetPrimaryUserTrackingId(SharedConstants.InvalidUserTrackingId, callbackLock);
                this.UpdateUserStates(callbackLock);
            }
        }
    }
}
