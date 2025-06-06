using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace VamTimeline
{
    public partial class AtomAnimation
    {
        #region Playback (API)

        public void PlayClipByName(string animationName, bool seq)
        {
            var clip = FindClipInPriorityOrder(animationName, playingAnimationSegmentId);
            if (clip == null)
            {
                if (logger.general) logger.Log(logger.generalCategory, $"Could not find animation '{animationName}' to play by name.");
                return;
            }
            DeactivateQueue();
            PlayClip(clip, seq);
        }

        public void PlayClipBySet(string animationName, string animationSet, string animationSegment, bool seq)
        {
            DeactivateQueue();
            if (!index.segmentNames.Contains(animationSegment))
                return;

            PlayClipBySet(animationName.ToId(), animationSet.ToId(), animationSegment.ToId(), seq);
        }

        public void PlayClipBySet(int animationNameId, int animationSetId, int animationSegmentId, bool seq)
        {
            DeactivateQueue();
            var siblings = GetMainAndBestSiblingPerLayer(animationSegmentId, animationNameId, animationSetId);

            if (animationSegmentId != playingAnimationSegmentId && animationSegmentId != AtomAnimationClip.SharedAnimationSegmentId && animationSegmentId != AtomAnimationClip.NoneAnimationSegmentId)
            {
                PlaySegment(siblings[0].target);
                for (var i = 0; i < siblings.Count; i++)
                {
                    siblings[i] = new TransitionTarget { target = siblings[i].target };
                }
            }

            for (var i = 0; i < siblings.Count; i++)
            {
                var clip = siblings[i];
                if (clip.target == null) continue;
                if (isPlaying && clip.main != null)
                    PlayClipCore(clip.main, clip.target, seq, true, false);
                else
                    PlayClipCore(null, clip.target, seq, true, false);
            }
        }

        public void PlayRandom(string groupName = null)
        {
            DeactivateQueue();
            var candidates = clips
                .Where(c => !c.playbackMainInLayer && (groupName == null || c.animationNameGroup == groupName))
                .ToList();

            if (candidates.Count == 0)
                return;

            var clip = SelectRandomClip(candidates);
            PlayClip(clip, true);
        }

        public void PlayClip(AtomAnimationClip clip, bool seq, bool allowPreserveLoops = true, bool startingQueue = false)
        {
            paused = false;
            if (clip.playbackMainInLayer) return;

            PlayClipCore(
                isPlaying
                    ? GetMainClipInLayer(index.ByLayerQualified(clip.animationLayerQualifiedId))
                    : null,
                clip,
                seq,
                allowPreserveLoops,
                true,
                startingQueue
            );
        }

        public void PlaySegment(string segmentName, bool seq = true)
        {
            DeactivateQueue();
            AtomAnimationsClipsIndex.IndexedSegment segment;
            if (!index.segmentsById.TryGetValue(segmentName.ToId(), out segment))
                return;
            PlaySegment(segment.mainClip, seq);
        }

        public void PlaySegment(AtomAnimationClip source, bool seq = true, bool startingQueue = false)
        {
            if (!startingQueue)
            {
                DeactivateQueue();
            }

            // Note: This needs to happen for other atoms to receive the peer message. Moving the invoke later (cleaner) will break existing scenes.
            sequencing = sequencing || seq;
            onSegmentPlayed.Invoke(source);

            var clipsToPlay = GetDefaultClipsPerLayer(source);

            _allowPlayingTermination = false;
            if (!source.isOnSharedSegment && source.animationSegmentId != playingAnimationSegmentId)
            {
                playingAnimationSegment = source.animationSegment;
                var hasPose = applyNextPose || clipsToPlay.Any(c => c.applyPoseOnTransition);
                if (hasPose)
                {
                    foreach (var clip in clips.Where(c => c.playbackEnabled && !c.isOnSharedSegment))
                    {
                        StopClip(clip);
                    }
                }
                else
                {
                    var blendOutDuration = clipsToPlay.FirstOrDefault(c => !c.isOnSharedSegment)?.blendInDuration ?? AtomAnimationClip.DefaultBlendDuration;
                    foreach (var clip in clips.Where(c => c.playbackMainInLayer && c.animationSegment != AtomAnimationClip.SharedAnimationSegment))
                    {
                        SoftStopClip(clip, blendOutDuration);
                    }
                }
            }
            _allowPlayingTermination = true;

            foreach (var clip in clipsToPlay)
            {
                PlayClipCore(null, clip, seq, false, false, startingQueue);
            }
        }

        #endregion

        #region Playback (Core)

        private void PlayClipCore(AtomAnimationClip previous, AtomAnimationClip next, bool seq, bool allowPreserveLoops, bool allowSibling, bool startingQueue = false)
        {
            if (!startingQueue)
            {
                DeactivateQueue();
            }

            paused = false;

            if (previous != null && !previous.playbackMainInLayer)
                throw new InvalidOperationException($"PlayClip must receive an initial clip that is the main on its layer. {previous.animationNameQualified}");

            if (isPlaying && next.playbackMainInLayer)
                return;

            var isPlayingChanged = false;

            if (!isPlaying)
            {
                _stopwatch.Reset();
                _stopwatch.Start();
                if (logger.peersSync && !syncWithPeers)
                    logger.Log(logger.peersSyncCategory, "Peer sync has been disabled on this atom");
                isPlayingChanged = true;
                isPlaying = true;
                Validate();
                sequencing = sequencing || seq;
                fadeManager?.SyncFadeTime();
                if (next.isOnSegment)
                {
                    PlaySegment(next, sequencing, startingQueue);
                }
            }

            if (next.isOnSegment && !IsPlayingAnimationSegment(next.animationSegmentId))
            {
                PlaySegment(next, sequencing, startingQueue);
                return;
            }

            if (!next.playbackEnabled && sequencing)
                next.clipTime = next.timeOffset;

            float blendInDuration;

            var nextHasPose = (applyNextPose || next.applyPoseOnTransition) && next.pose != null;

            if (previous != null)
            {
                if (previous.uninterruptible)
                {
                    if (logger.triggersReceived)
                        logger.Log(logger.triggersCategory, $"Prevented '{next.animationNameQualified}' from playing because '{previous.animationNameQualified}' has prevent trigger interruptions enabled.");
                    return;
                }

                // Wait for the loop to sync or the non-loop to end
                if (allowPreserveLoops && !nextHasPose)
                {
                    if (previous.loop && previous.preserveLoops && next.preserveLoopsOrLength)
                    {
                        var nextTime = next.loop
                            ? previous.animationLength - next.blendInDuration / 2f - previous.clipTime
                            : previous.animationLength - next.blendInDuration - previous.clipTime;
                        if (nextTime < 0)
                        {
                            if (next.loop)
                                nextTime += previous.animationLength;
                            else
                                nextTime = float.Epsilon;
                        }
                        ScheduleNextAnimation(previous, next, nextTime);
                        return;
                    }

                    if (!previous.loop && previous.preserveLength)
                    {
                        var nextTime = Mathf.Max(previous.animationLength - next.blendInDuration - previous.clipTime, 0f);
                        ScheduleNextAnimation(previous, next, nextTime);
                        return;
                    }
                }

                previous.playbackMainInLayer = false;
                previous.playbackScheduledNextAnimation = null;
                previous.playbackScheduledNextTimeLeft = float.NaN;

                // Blend immediately, but unlike TransitionClips, recording will ignore blending
                blendInDuration = next.recording || nextHasPose ? 0f : next.blendInDuration;
                BlendOut(previous, blendInDuration);
            }
            else
            {
                // Blend immediately (first animation to play on that layer)
                blendInDuration = next.recording ? 0f : next.blendInDuration;
            }

            next.playbackMainInLayer = true;
            BlendIn(next, blendInDuration);

            if (next.animationPattern)
            {
                next.animationPattern.SetBoolParamValue("loopOnce", false);
                next.animationPattern.ResetAndPlay();
            }

            if (isPlayingChanged)
            {
                onIsPlayingChanged.Invoke(next);
                isPlayingChangedTrigger.SetActive(true);
            }


            onMainClipPerLayerChanged.Invoke(new AtomAnimationChangeClipEventArgs { before = previous, after = next });

            if (allowSibling && nextHasPose && previous != null)
            {
                foreach (var c in index.GetSiblingsByLayer(previous))
                {
                    c.playbackMainInLayer = false;
                    c.playbackScheduledNextAnimation = null;
                    c.playbackScheduledNextTimeLeft = float.NaN;
                    BlendOut(c, 0);
                    if (c.playbackMainInLayer)
                        onMainClipPerLayerChanged.Invoke(new AtomAnimationChangeClipEventArgs { before = c, after = null });
                }
            }

            if (allowSibling && (sequencing || !focusOnLayer) && !isQueueActive)
                PlaySiblings(next);
        }

        private void Validate()
        {
            foreach (var controllerRef in animatables.controllers)
            {
                if (controllerRef.owned) continue;
                if (controllerRef.controller == null)
                    throw new InvalidOperationException("Timeline: An external controller has been removed. Remove it from Timeline to restore playback.");
            }
            foreach (var floatParamRef in animatables.storableFloats)
            {
                if (!floatParamRef.EnsureAvailable(silent: false, forceCheck: true))
                    SuperController.LogError($"Timeline: The storable float '{floatParamRef.GetFullName()}' has been removed. Remove it from Timeline to silence this error.");
            }
        }

        private void PlaySiblings(AtomAnimationClip clip)
        {
            var clipsByName = index.segmentsById[clip.animationSegmentId].clipMapByNameId[clip.animationNameId];

            var clipTime = clip.clipTime - clip.timeOffset;
            PlaySiblingsByName(clipsByName, clipTime);
            PlaySiblingsBySet(clip, clipsByName, clipTime);
        }

        private void PlaySiblingsByName(IList<AtomAnimationClip> clipsByName, float clipTime)
        {
            if (clipsByName.Count == 1) return;
            for (var i = 0; i < clipsByName.Count; i++)
            {
                var clip = clipsByName[i];
                if (clip.playbackMainInLayer) continue;
                TransitionClips(
                    GetMainClipInLayer(index.ByLayerQualified(clip.animationLayerQualifiedId)),
                    clip,
                    clipTime);
            }
        }

        private void PlaySiblingsBySet(AtomAnimationClip clip, IList<AtomAnimationClip> clipsByName, float clipTime)
        {
            if (clip.animationSet == null) return;
            var layers = index.segmentsById[clip.animationSegmentId].layers;
            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (LayerContainsClip(clipsByName, layer[0].animationLayerQualified)) continue;
                var sibling = GetSiblingInLayer(layer, clip.animationSet);
                if (sibling == null) continue;
                var main = GetMainClipInLayer(layer);
                TransitionClips(main, sibling, clipTime);
            }
        }

        private static bool LayerContainsClip(IList<AtomAnimationClip> clipsByName, string animationLayerQualified)
        {
            for (var j = 0; j < clipsByName.Count; j++)
            {
                if (clipsByName[j].animationLayerQualified == animationLayerQualified)
                    return true;
            }
            return false;
        }

        public void SoftStopClip(AtomAnimationClip clip, float blendOutDuration)
        {
            clip.playbackMainInLayer = false;
            clip.playbackScheduledNextAnimation = null;
            clip.playbackScheduledNextTimeLeft = float.NaN;
            BlendOut(clip, blendOutDuration);
            onMainClipPerLayerChanged.Invoke(new AtomAnimationChangeClipEventArgs { before = clip, after = null });
        }

        private void StopClip(AtomAnimationClip clip)
        {
            if (clip.playbackEnabled)
            {
                if (logger.general) logger.Log(logger.generalCategory, $"Leave '{clip.animationNameQualified}' (stop)");
                clip.Leave();
                clip.Reset(false);
                if (clip.animationPattern)
                    clip.animationPattern.SetBoolParamValue("loopOnce", true);
                onClipIsPlayingChanged.Invoke(clip);
            }
            else
            {
                clip.playbackMainInLayer = false;
                onMainClipPerLayerChanged.Invoke(new AtomAnimationChangeClipEventArgs { before = clip, after = null });
            }

            if (_allowPlayingTermination && isPlaying)
            {
                if (!clips.Any(c => c.playbackMainInLayer))
                {
                    if (logger.general) logger.Log(logger.generalCategory, $"No animations currently playing, stopping Timeline");
                    isPlaying = false;
                    sequencing = false;
                    paused = false;
                    applyNextPose = false;
                    DeactivateQueue();
                    onIsPlayingChanged.Invoke(clip);
                    isPlayingChangedTrigger.SetActive(false);
                    _stopwatch.Stop();
                    _stopwatch.Reset();
                }
            }
        }

        public void StopAll()
        {
            _allowPlayingTermination = true;
            autoStop = 0f;
            DeactivateQueue();

            foreach (var clip in clips)
            {
                StopClip(clip);
            }
            playTime = 0f;
            foreach (var clip in clips)
            {
                clip.Reset(false);
            }

            if (fadeManager?.black == true)
            {
                _scheduleFadeIn = float.MaxValue;
                fadeManager.FadeIn();
            }
        }

        public void ResetAll()
        {
            DeactivateQueue();
            playTime = 0f;
            foreach (var clip in clips)
                clip.Reset(true);
        }

        public void StopAndReset()
        {
            DeactivateQueue();
            if (isPlaying) StopAll();
            ResetAll();
        }

        #endregion

        public void DeactivateQueue()
        {
            if (isQueueActive && logger.sequencing) logger.Log(logger.sequencingCategory, "Deactivating animation queue.");
            isQueueActive = false;
            queueIndex = -1;
        }

        #region Animation state

        private void AdvanceClipsTime(float delta)
        {
            if (delta == 0) return;

            var layers = index.clipsGroupedByLayer;
            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                var layerClips = layers[layerIndex];
                float clipSpeed;
                if (layerClips.Count > 1)
                {
                    var weightedClipSpeedSum = 0f;
                    var totalBlendWeights = 0f;
                    clipSpeed = 0f;
                    for (var i = 0; i < layerClips.Count; i++)
                    {
                        var clip = layerClips[i];
                        if (!clip.playbackEnabled) continue;
                        var blendWeight = clip.playbackBlendWeightSmoothed;
                        weightedClipSpeedSum += clip.speed * blendWeight;
                        totalBlendWeights += blendWeight;
                        clipSpeed = clip.speed;
                    }

                    clipSpeed = weightedClipSpeedSum == 0 ? clipSpeed : weightedClipSpeedSum / totalBlendWeights;
                }
                else
                {
                    clipSpeed = layerClips[0].speed;
                }

                for (var i = 0; i < layerClips.Count; i++)
                {
                    var clip = layerClips[i];
                    if (!clip.playbackEnabled) continue;

                    var clipDelta = delta * clipSpeed;
                    if (!ReferenceEquals(clip.audioSourceControl, null))
                    {
                        var audioTime = clip.audioSourceControl.audioSource.time;
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        if (audioTime == clip.clipTime)
                        {
                            clip.clipTime += clipDelta * clip.audioSourceControl.audioSource.pitch;
                        }
                        else
                        {
                            clip.clipTime = audioTime;
                        }
                    }
                    else
                    {
                        clip.clipTime += clipDelta;
                    }

                    if (clip.playbackBlendRate != 0)
                    {
                        clip.playbackBlendWeight += clip.playbackBlendRate * Mathf.Abs(clipDelta);
                        if (clip.playbackBlendWeight >= clip.weight)
                        {
                            clip.playbackBlendRate = 0f;
                            clip.playbackBlendWeight = clip.weight;
                        }
                        else if (clip.playbackBlendWeight <= 0f)
                        {
                            if (!float.IsNaN(clip.playbackScheduledNextTimeLeft))
                            {
                                // Wait for the sequence time to be reached
                                clip.playbackBlendWeight = 0;
                            }
                            else
                            {
                                if (logger.general) logger.Log(logger.generalCategory, $"Leave '{clip.animationNameQualified}' (blend out complete)");
                                clip.Leave();
                                clip.Reset(true);
                                onClipIsPlayingChanged.Invoke(clip);
                            }
                        }
                    }
                }
            }
        }

        private void BlendIn(AtomAnimationClip clip, float blendDuration)
        {
            if (applyNextPose || clip.applyPoseOnTransition)
            {
                if (!clip.recording && clip.pose != null && (sequencing || lastAppliedPose != clip.pose))
                {
                    if (logger.sequencing)
                        logger.Log(logger.sequencingCategory, $"Applying pose '{clip.animationNameQualified}'");
                    clip.pose.Apply();
                    lastAppliedPose = clip.pose;
                }
                clip.playbackBlendWeight = 1f;
                clip.playbackBlendRate = 0f;
            }
            else if (blendDuration == 0)
            {
                clip.playbackBlendWeight = 1f;
                clip.playbackBlendRate = 0f;
            }
            else
            {
                if (!clip.playbackEnabled) clip.playbackBlendWeight = float.Epsilon;
                clip.playbackBlendRate = forceBlendTime
                    ? (1f - clip.playbackBlendWeight) / blendDuration
                    : 1f / blendDuration;
            }

            if (clip.playbackEnabled) return;

            clip.playbackEnabled = true;
            clip.playbackPassedZero = clip.clipTime == 0f;
            if (logger.general) logger.Log(logger.generalCategory, $"Enter '{clip.animationNameQualified}'");
            onClipIsPlayingChanged.Invoke(clip);
            if (logger.showPlayInfoInHelpText)
            {
                if (index.segmentIds.Count > 1)
                    logger.ShowTemporaryMessage($"Timeline: Play {clip.animationNameQualified}");
                else if (index.ByName(clip.animationSegmentId, clip.animationNameId).Count == 1)
                    logger.ShowTemporaryMessage($"Timeline: Play {clip.animationName}");
                else
                    logger.ShowTemporaryMessage($"Timeline: Play {clip.animationLayer} / {clip.animationName}");
            }
        }

        private void BlendOut(AtomAnimationClip clip, float blendDuration)
        {
            if (!clip.playbackEnabled) return;

            if (blendDuration == 0 || clip.playbackBlendWeight == 0)
            {
                if (logger.general) logger.Log(logger.generalCategory, $"Leave '{clip.animationNameQualified}' (immediate blend out)");
                clip.Leave();
                clip.Reset(true);
            }
            else
            {
                clip.playbackBlendRate = forceBlendTime
                    ? (-1f - clip.playbackBlendWeight) / blendDuration
                    : -1f / blendDuration;
            }
        }

        #endregion

        #region Sampling

        public bool RebuildPending()
        {
            return _animationRebuildRequestPending || _animationRebuildInProgress;
        }

        public void Sample()
        {
            if (isPlaying && !paused || !enabled) return;

            SampleFloatParams();
            SampleControllers(true);
        }

        private void SyncTriggers(bool live)
        {
            for (var clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                var clip = clips[clipIndex];
                var triggersCount = clip.targetTriggers.Count;
                for (var triggerIndex = 0; triggerIndex < triggersCount; triggerIndex++)
                {
                    var target = clip.targetTriggers[triggerIndex];
                    if (target.animatableRef.live != live) continue;
                    //if (target.animatableRef.activateThroughStartOnly && !clip.playbackPassedZero) continue;
                    if (clip.playbackEnabled)
                    {
                        target.Sync(clip.clipTime, live, clip.loop);
                    }
                    target.Update();
                }
            }
        }

        [MethodImpl(256)]
        private void SampleFloatParams()
        {
            if (simulationFrozen) return;
            if (_globalScaledWeight <= 0) return;
            foreach (var x in index.ByFloatParam())
            {
                if (!x.Value[0].animatableRef.EnsureAvailable()) continue;
                SampleFloatParam(x.Value[0].animatableRef, x.Value);
            }
        }

        [MethodImpl(256)]
        private void SampleFloatParam(JSONStorableFloatRef floatParamRef, List<JSONStorableFloatAnimationTarget> targets)
        {
            const float minimumDelta = 0.00000015f;
            var weightedSum = 0f;
            var totalBlendWeights = 0f;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var clip = target.clip;
                if (target.recording)
                {
                    target.SetKeyframeToCurrent(clip.clipTime.Snap(), false);
                    return;
                }
                if (!clip.playbackEnabled && !clip.temporarilyEnabled) continue;
                var localScaledWeight = clip.temporarilyEnabled ? 1f : clip.scaledWeight;
                if (localScaledWeight < float.Epsilon) continue;

                var value = target.value.Evaluate(clip.clipTime);
                var blendWeight = clip.temporarilyEnabled ? 1f : clip.playbackBlendWeightSmoothed;
                weightedSum += Mathf.Lerp(floatParamRef.val, value, localScaledWeight) * blendWeight;
                totalBlendWeights += blendWeight;
            }

            if (totalBlendWeights > minimumDelta)
            {
                var val = weightedSum / totalBlendWeights;
                if (Mathf.Abs(val - floatParamRef.val) > minimumDelta)
                {
                    floatParamRef.val = Mathf.Lerp(floatParamRef.val, val, _globalScaledWeight);
                }
            }
        }

        [MethodImpl(256)]
        private void SampleControllers(bool force = false)
        {
            if (simulationFrozen) return;
            if (_globalScaledWeight <= 0) return;
            foreach (var x in index.ByController())
            {
                SampleController(x.Key.controller, x.Value, force, force ? 1f : x.Key.scaledPositionWeight, force ? 1f : x.Key.scaledRotationWeight);
            }
        }

        public void SampleParentedControllers(AtomAnimationClip source)
        {
            if (simulationFrozen) return;
            if (_globalScaledWeight <= 0) return;
            // TODO: Index keep track if there is any parenting
            if (source == null) return;
            var layers = GetMainAndBestSiblingPerLayer(playingAnimationSegmentId, source.animationNameId, source.animationSetId);
            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                var clip = layers[layerIndex];
                if (clip.target == null) continue;
                for (var controllerIndex = 0; controllerIndex < clip.target.targetControllers.Count; controllerIndex++)
                {
                    var ctrl = clip.target.targetControllers[controllerIndex];
                    if (!ctrl.EnsureParentAvailable()) continue;
                    if (!ctrl.hasParentBound) continue;

                    var controller = ctrl.animatableRef.controller;
                    if (controller.isGrabbing) continue;
                    var positionRB = ctrl.GetPositionParentRB();
                    if (!ReferenceEquals(positionRB, null))
                    {
                        var targetPosition = positionRB.transform.TransformPoint(ctrl.EvaluatePosition(source.clipTime));
                        if (controller.currentPositionState != FreeControllerV3.PositionState.Off)
                            controller.control.position = Vector3.Lerp(controller.control.position, targetPosition, _globalWeight);
                    }
                    var rotationParentRB = ctrl.GetRotationParentRB();
                    if (!ReferenceEquals(rotationParentRB, null))
                    {
                        var targetRotation = rotationParentRB.rotation * ctrl.EvaluateRotation(source.clipTime);
                        if (controller.currentRotationState != FreeControllerV3.RotationState.Off)
                            controller.control.rotation = Quaternion.Slerp(controller.control.rotation, targetRotation, _globalWeight);
                    }
                }
            }
        }

        private Quaternion[] _rotations = new Quaternion[0];
        private float[] _rotationBlendWeights = new float[0];

        [MethodImpl(256)]
        private void SampleController(FreeControllerV3 controller, IList<FreeControllerV3AnimationTarget> targets, bool force, float animatablePositionWeight, float animatableRotationWeight)
        {
            if (ReferenceEquals(controller, null)) return;
            var control = controller.control;

            if (targets.Count > _rotations.Length)
            {
                _rotations = new Quaternion[targets.Count];
                _rotationBlendWeights = new float[targets.Count];
            }
            var rotationCount = 0;
            var totalRotationBlendWeights = 0f;
            var totalRotationControlWeights = 0f;

            var weightedPositionSum = Vector3.zero;
            var totalPositionBlendWeights = 0f;
            var totalPositionControlWeights = 0f;
            var animatedCount = 0;

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var clip = target.clip;
                if (target.recording)
                {
                    target.SetKeyframeToCurrent(clip.clipTime.Snap(), false);
                    continue;
                }
                if (!clip.playbackEnabled && !clip.temporarilyEnabled) continue;
                if (!target.playbackEnabled) continue;
                if (controller.possessed) return;
                if (controller.isGrabbing) return;
                var weight = clip.temporarilyEnabled ? 1f : clip.scaledWeight * target.scaledWeight;
                if (weight < float.Epsilon) continue;

                if (!target.EnsureParentAvailable()) return;

                var blendWeight = clip.temporarilyEnabled ? 1f : clip.playbackBlendWeightSmoothed;

                if (target.targetsRotation && target.controlRotation && controller.currentRotationState != FreeControllerV3.RotationState.Off)
                {
                    var rotLink = target.GetPositionParentRB();
                    var hasRotLink = !ReferenceEquals(rotLink, null);

                    var targetRotation = target.EvaluateRotation(clip.clipTime);
                    if (hasRotLink)
                    {
                        targetRotation = rotLink.rotation * targetRotation;
                        _rotations[rotationCount] = targetRotation;
                    }
                    else
                    {
                        _rotations[rotationCount] = control.transform.parent.rotation * targetRotation;
                    }

                    _rotationBlendWeights[rotationCount] = blendWeight;
                    totalRotationBlendWeights += blendWeight;
                    totalRotationControlWeights += weight * blendWeight;
                    rotationCount++;
                }

                if (target.targetsPosition && target.controlPosition && controller.currentPositionState != FreeControllerV3.PositionState.Off)
                {
                    var posLink = target.GetPositionParentRB();
                    var hasPosLink = !ReferenceEquals(posLink, null);

                    var targetPosition = target.EvaluatePosition(clip.clipTime);
                    if (hasPosLink)
                    {
                        targetPosition = posLink.transform.TransformPoint(targetPosition);
                    }
                    else
                    {
                        targetPosition = control.transform.parent.TransformPoint(targetPosition);
                    }

                    weightedPositionSum += targetPosition * blendWeight;
                    totalPositionBlendWeights += blendWeight;
                    totalPositionControlWeights += weight * blendWeight;
                    animatedCount++;
                }
            }

            if (totalRotationBlendWeights > float.Epsilon && controller.currentRotationState != FreeControllerV3.RotationState.Off)
            {
                Quaternion targetRotation;
                if (rotationCount > 1)
                {
                    var cumulative = Vector4.zero;
                    for (var i = 0; i < rotationCount; i++)
                    {
                        QuaternionUtil.AverageQuaternion(ref cumulative, _rotations[i], _rotations[0], _rotationBlendWeights[i] / totalRotationBlendWeights);
                    }
                    targetRotation = QuaternionUtil.FromVector(cumulative);
                }
                else
                {
                    targetRotation = _rotations[0];
                }

                var controlWeight = animatedCount == 1 ? totalRotationControlWeights : totalRotationControlWeights / totalRotationBlendWeights;
                var finalWeight = controlWeight * _globalScaledWeight * animatableRotationWeight;

                var rotation = Quaternion.Slerp(control.rotation, targetRotation, finalWeight);
                control.rotation = rotation;
            }

            if (totalPositionBlendWeights > float.Epsilon && controller.currentPositionState != FreeControllerV3.PositionState.Off)
            {
                var targetPosition = weightedPositionSum / totalPositionBlendWeights;
                var controlWeight = animatedCount == 1 ? totalPositionControlWeights : (totalPositionControlWeights / totalPositionBlendWeights);
                var finalWeight = controlWeight * _globalScaledWeight * animatablePositionWeight;

                var position = Vector3.Lerp(control.position, targetPosition, finalWeight);
                control.position = position;
            }

            if (force && (controller.currentPositionState == FreeControllerV3.PositionState.Comply ||
                controller.currentRotationState == FreeControllerV3.RotationState.Comply))
            {
                controller.PauseComply();
            }
        }

        #endregion
    }
}
