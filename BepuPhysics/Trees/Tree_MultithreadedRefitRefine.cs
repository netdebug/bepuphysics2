﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace BepuPhysics.Trees
{
    partial struct Tree
    {
        /// <summary>
        /// Caches input and output for the multithreaded execution of a tree's refit and refinement operations.
        /// </summary>
        public class RefitAndRefineMultithreadedContext
        {
            Tree Tree;

            int RefitNodeIndex;
            QuickList<int, Buffer<int>> RefitNodes;
            float RefitCostChange;

            int RefinementLeafCountThreshold;
            Buffer<QuickList<int, Buffer<int>>> RefinementCandidates;
            Action<int> RefitAndMarkAction;

            int RefineIndex;
            QuickList<int, Buffer<int>> RefinementTargets;
            int MaximumSubtrees;
            Action<int> RefineAction;

            QuickList<int, Buffer<int>> CacheOptimizeStarts;
            int PerWorkerCacheOptimizeCount;
            Action<int> CacheOptimizeAction;

            IThreadDispatcher threadDispatcher;

            public RefitAndRefineMultithreadedContext()
            {
                RefitAndMarkAction = RefitAndMark;
                RefineAction = Refine;
                CacheOptimizeAction = CacheOptimize;
            }

            public unsafe void RefitAndRefine(ref Tree tree, BufferPool pool, IThreadDispatcher threadDispatcher, int frameIndex,
                float refineAggressivenessScale = 1, float cacheOptimizeAggressivenessScale = 1)
            {
                if (tree.leafCount <= 2)
                {
                    //If there are 2 or less leaves, then refit/refine/cache optimize doesn't do anything at all.
                    //(The root node has no parent, so it does not have a bounding box, and the SAH won't change no matter how we swap the children of the root.)
                    //Avoiding this case also gives the other codepath a guarantee that it will be working with nodes with two children.
                    return;
                }
                this.threadDispatcher = threadDispatcher;
                Tree = tree;
                //Note that we create per-thread refinement candidates. That's because candidates are found during the multithreaded refit and mark phase, and 
                //we don't want to spend the time doing sync work. The candidates are then pruned down to a target single target set for the refine pass.
                pool.Take(threadDispatcher.ThreadCount, out RefinementCandidates);
                Tree.GetRefitAndMarkTuning(out MaximumSubtrees, out var estimatedRefinementCandidateCount, out RefinementLeafCountThreshold);
                //Note that the number of refit nodes is not necessarily bound by MaximumSubtrees. It is just a heuristic estimate. Resizing has to be supported.
                QuickList<int, Buffer<int>>.Create(pool.SpecializeFor<int>(), MaximumSubtrees, out RefitNodes);
                //Note that we haven't rigorously guaranteed a refinement count maximum, so it's possible that the workers will need to resize the per-thread refinement candidate lists.
                for (int i = 0; i < threadDispatcher.ThreadCount; ++i)
                {
                    QuickList<int, Buffer<int>>.Create(threadDispatcher.GetThreadMemoryPool(i).SpecializeFor<int>(), estimatedRefinementCandidateCount, out RefinementCandidates[i]);
                }

                int multithreadingLeafCountThreshold = Tree.leafCount / (threadDispatcher.ThreadCount * 2);
                if (multithreadingLeafCountThreshold < RefinementLeafCountThreshold)
                    multithreadingLeafCountThreshold = RefinementLeafCountThreshold;
                CollectNodesForMultithreadedRefit(0, multithreadingLeafCountThreshold, ref RefitNodes, RefinementLeafCountThreshold, ref RefinementCandidates[0],
                    pool, threadDispatcher.GetThreadMemoryPool(0).SpecializeFor<int>());

                RefitNodeIndex = -1;
                threadDispatcher.DispatchWorkers(RefitAndMarkAction);
                //Condense the set of candidates into a set of targets.
                int refinementCandidatesCount = 0;
                for (int i = 0; i < threadDispatcher.ThreadCount; ++i)
                {
                    refinementCandidatesCount += RefinementCandidates[i].Count;
                }
                Tree.GetRefineTuning(frameIndex, refinementCandidatesCount, refineAggressivenessScale, RefitCostChange,
                    out var targetRefinementCount, out var period, out var offset);
                QuickList<int, Buffer<int>>.Create(pool.SpecializeFor<int>(), targetRefinementCount, out RefinementTargets);

                //Note that only a subset of all refinement *candidates* will become refinement *targets*.
                //We start at a semirandom offset and then skip through the set to accumulate targets.
                //The number of candidates that become targets is based on the refinement aggressiveness,
                //tuned by both user input (the scale) and on the volatility of the tree (RefitCostChange).
                var currentCandidatesIndex = 0;
                int index = offset;
                for (int i = 0; i < targetRefinementCount - 1; ++i)
                {
                    index += period;
                    //Wrap around if the index doesn't fit.
                    while (index >= RefinementCandidates[currentCandidatesIndex].Count)
                    {
                        index -= RefinementCandidates[currentCandidatesIndex].Count;
                        ++currentCandidatesIndex;
                        if (currentCandidatesIndex >= threadDispatcher.ThreadCount)
                            currentCandidatesIndex -= threadDispatcher.ThreadCount;
                    }
                    Debug.Assert(index < RefinementCandidates[currentCandidatesIndex].Count && index >= 0);
                    var nodeIndex = RefinementCandidates[currentCandidatesIndex][index];
                    Debug.Assert(Tree.metanodes[nodeIndex].RefineFlag == 0, "Refinement target search shouldn't run into the same node twice!");
                    RefinementTargets.AddUnsafely(nodeIndex);
                    Tree.metanodes[nodeIndex].RefineFlag = 1;
                }
                //Note that the root node is only refined if it was not picked as a target earlier.
                if (Tree.metanodes->RefineFlag != 1)
                {
                    RefinementTargets.AddUnsafely(0);
                    Tree.metanodes->RefineFlag = 1;
                }
                RefineIndex = -1;

                threadDispatcher.DispatchWorkers(RefineAction);
                //Note that we defer the refine flag clear until after the refinements complete. If we did it within the refine action itself, 
                //it would introduce nondeterminism by allowing refines to progress based on their order of completion.
                for (int i = 0; i < RefinementTargets.Count; ++i)
                {
                    Tree.metanodes[RefinementTargets[i]].RefineFlag = 0;
                }

                //To multithread this, give each worker a contiguous chunk of nodes. You want to do the biggest chunks possible to chain decent cache behavior as far as possible.
                //Note that more cache optimization is required with more threads, since spreading it out more slightly lessens its effectiveness.
                var cacheOptimizeCount = Tree.GetCacheOptimizeTuning(MaximumSubtrees, RefitCostChange, (Math.Max(1, threadDispatcher.ThreadCount * 0.25f)) * cacheOptimizeAggressivenessScale);

                var cacheOptimizationTasks = threadDispatcher.ThreadCount * 2;
                PerWorkerCacheOptimizeCount = cacheOptimizeCount / cacheOptimizationTasks;
                var startIndex = (int)(((long)frameIndex * PerWorkerCacheOptimizeCount) % Tree.nodeCount);
                QuickList<int, Buffer<int>>.Create(pool.SpecializeFor<int>(), cacheOptimizationTasks, out CacheOptimizeStarts);
                CacheOptimizeStarts.AddUnsafely(startIndex);

                var optimizationSpacing = Tree.nodeCount / threadDispatcher.ThreadCount;
                var optimizationSpacingWithExtra = optimizationSpacing + 1;
                var optimizationRemainder = Tree.nodeCount - optimizationSpacing * threadDispatcher.ThreadCount;

                for (int i = 1; i < cacheOptimizationTasks; ++i)
                {
                    if (optimizationRemainder > 0)
                    {
                        startIndex += optimizationSpacingWithExtra;
                        --optimizationRemainder;
                    }
                    else
                    {
                        startIndex += optimizationSpacing;
                    }
                    if (startIndex >= Tree.nodeCount)
                        startIndex -= Tree.nodeCount;
                    Debug.Assert(startIndex >= 0 && startIndex < Tree.nodeCount);
                    CacheOptimizeStarts.AddUnsafely(startIndex);
                }

                threadDispatcher.DispatchWorkers(CacheOptimizeAction);

                for (int i = 0; i < threadDispatcher.ThreadCount; ++i)
                {
                    //Note the use of the thread memory pool. Each thread allocated their own memory for the list since resizes were possible.
                    RefinementCandidates[i].Dispose(threadDispatcher.GetThreadMemoryPool(i).SpecializeFor<int>());
                }
                pool.Return(ref RefinementCandidates);
                RefitNodes.Dispose(pool.SpecializeFor<int>());
                RefinementTargets.Dispose(pool.SpecializeFor<int>());
                CacheOptimizeStarts.Dispose(pool.SpecializeFor<int>());
                Tree = default;
                this.threadDispatcher = null;
            }

            unsafe void CollectNodesForMultithreadedRefit(int nodeIndex,
                int multithreadingLeafCountThreshold, ref QuickList<int, Buffer<int>> refitAndMarkTargets,
                int refinementLeafCountThreshold, ref QuickList<int, Buffer<int>> refinementCandidates, BufferPool pool, BufferPool<int> threadIntPool)
            {
                var node = Tree.nodes + nodeIndex;
                var metanode = Tree.metanodes + nodeIndex;
                var children = &node->A;
                Debug.Assert(metanode->RefineFlag == 0);
                Debug.Assert(Tree.leafCount > 2);
                for (int i = 0; i < 2; ++i)
                {
                    ref var child = ref children[i];
                    if (child.Index >= 0)
                    {
                        //Each node stores how many children are involved in the multithreaded refit.
                        //This allows the postphase to climb the tree in a thread safe way.
                        ++metanode->RefineFlag;
                        if (child.LeafCount <= multithreadingLeafCountThreshold)
                        {
                            if (child.LeafCount <= refinementLeafCountThreshold)
                            {
                                //It's possible that a wavefront node is this high in the tree, so it has to be captured here because the postpass won't find it.
                                refinementCandidates.Add(child.Index, threadIntPool);
                                //Encoding the child index tells the thread to use RefitAndMeasure instead of RefitAndMark since this was a wavefront node.
                                refitAndMarkTargets.Add(Encode(child.Index), pool.SpecializeFor<int>());
                            }
                            else
                            {
                                refitAndMarkTargets.Add(child.Index, pool.SpecializeFor<int>());
                            }
                        }
                        else
                        {
                            CollectNodesForMultithreadedRefit(child.Index, multithreadingLeafCountThreshold, ref refitAndMarkTargets, refinementLeafCountThreshold, ref refinementCandidates, pool, threadIntPool);
                        }
                    }
                }
            }

            unsafe void RefitAndMark(int workerIndex)
            {
                //Since resizes may occur, we have to use the thread's buffer pool.
                //The main thread already created the refinement candidate list using the worker's pool.
                var threadIntPool = threadDispatcher.GetThreadMemoryPool(workerIndex).SpecializeFor<int>();
                int refitIndex;
                Debug.Assert(Tree.leafCount > 2);
                while ((refitIndex = Interlocked.Increment(ref RefitNodeIndex)) < RefitNodes.Count)
                {

                    var nodeIndex = RefitNodes[refitIndex];
                    bool shouldUseMark;
                    if (nodeIndex < 0)
                    {
                        //Node was already marked as a wavefront. Should proceed with a RefitAndMeasure instead of RefitAndMark.
                        nodeIndex = Encode(nodeIndex);
                        shouldUseMark = false;
                    }
                    else
                    {
                        shouldUseMark = true;
                    }

                    var node = Tree.nodes + nodeIndex;
                    var metanode = Tree.metanodes + nodeIndex;
                    Debug.Assert(metanode->Parent >= 0, "The root should not be marked for refit.");
                    var parent = Tree.nodes + metanode->Parent;
                    var childInParent = &parent->A + metanode->IndexInParent;
                    if (shouldUseMark)
                    {
                        var costChange = Tree.RefitAndMark(ref *childInParent, RefinementLeafCountThreshold, ref RefinementCandidates[workerIndex], threadIntPool);
                        metanode->LocalCostChange = costChange;
                    }
                    else
                    {
                        var costChange = Tree.RefitAndMeasure(ref *childInParent);
                        metanode->LocalCostChange = costChange;
                    }


                    //int foundLeafCount;
                    //Tree.Validate(RefitNodes.Elements[refitNodeIndex], node->Parent, node->IndexInParent, ref *boundingBoxInParent, out foundLeafCount);


                    //Walk up the tree.
                    node = parent;
                    metanode = Tree.metanodes + metanode->Parent;
                    while (true)
                    {

                        if (Interlocked.Decrement(ref metanode->RefineFlag) == 0)
                        {
                            //Compute the child contributions to this node's volume change.
                            var children = &node->A;
                            metanode->LocalCostChange = 0;
                            for (int i = 0; i < 2; ++i)
                            {
                                ref var child = ref children[i];
                                if (child.Index >= 0)
                                {
                                    var childMetadata = Tree.metanodes + child.Index;
                                    metanode->LocalCostChange += childMetadata->LocalCostChange;
                                    //Clear the refine flag (unioned).
                                    childMetadata->RefineFlag = 0;

                                }
                            }

                            //This thread is the last thread to visit this node, so it must handle this node.
                            //Merge all the child bounding boxes into one. 
                            if (metanode->Parent < 0)
                            {
                                //Root node.
                                //Don't bother including the root's change in volume.
                                //Refinement can't change the root's bounds, so the fact that the world got bigger or smaller
                                //doesn't really have any bearing on how much refinement should be done.
                                //We do, however, need to divide by root volume so that we get the change in cost metric rather than volume.
                                var merged = new BoundingBox { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
                                for (int i = 0; i < 2; ++i)
                                {
                                    ref var child = ref children[i];
                                    BoundingBox.CreateMerged(child.Min, child.Max, merged.Min, merged.Max, out merged.Min, out merged.Max);
                                }
                                var postmetric = ComputeBoundsMetric(ref merged);
                                if (postmetric > 1e-9f)
                                    RefitCostChange = metanode->LocalCostChange / postmetric;
                                else
                                    RefitCostChange = 0;
                                //Clear the root's refine flag (unioned).
                                metanode->RefineFlag = 0;
                                break;
                            }
                            else
                            {
                                parent = Tree.nodes + metanode->Parent;
                                childInParent = &parent->A + metanode->IndexInParent;
                                var premetric = ComputeBoundsMetric(ref childInParent->Min, ref childInParent->Max);
                                childInParent->Min = new Vector3(float.MaxValue);
                                childInParent->Max = new Vector3(float.MinValue);
                                for (int i = 0; i < 2; ++i)
                                {
                                    ref var child = ref children[i];
                                    BoundingBox.CreateMerged(child.Min, child.Max,  childInParent->Min, childInParent->Max, out childInParent->Min, out childInParent->Max);
                                }
                                var postmetric = ComputeBoundsMetric(ref childInParent->Min, ref childInParent->Max);
                                metanode->LocalCostChange += postmetric - premetric;
                                node = parent;
                                metanode = Tree.metanodes + metanode->Parent;
                            }
                        }
                        else
                        {
                            //This thread wasn't the last to visit this node, so it should die. Some other thread will handle it later.
                            break;
                        }
                    }

                }


            }

            unsafe void Refine(int workerIndex)
            {
                var threadPool = threadDispatcher.GetThreadMemoryPool(workerIndex);
                var threadIntPool = threadPool.SpecializeFor<int>();
                var subtreeCountEstimate = 1 << SpanHelper.GetContainingPowerOf2(MaximumSubtrees);
                QuickList<int, Buffer<int>>.Create(threadIntPool, subtreeCountEstimate, out var subtreeReferences);
                QuickList<int, Buffer<int>>.Create(threadIntPool, subtreeCountEstimate, out var treeletInternalNodes);

                CreateBinnedResources(threadPool, MaximumSubtrees, out var buffer, out var resources);

                int refineIndex;
                while ((refineIndex = Interlocked.Increment(ref RefineIndex)) < RefinementTargets.Count)
                {
                    Tree.BinnedRefine(RefinementTargets[refineIndex], ref subtreeReferences, MaximumSubtrees, ref treeletInternalNodes, ref resources, threadPool);
                    subtreeReferences.Count = 0;
                    treeletInternalNodes.Count = 0;
                }

                subtreeReferences.Dispose(threadIntPool);
                treeletInternalNodes.Dispose(threadIntPool);
                threadPool.Return(ref buffer);


            }

            void CacheOptimize(int workerIndex)
            {
                var startIndex = CacheOptimizeStarts[workerIndex];

                //We could wrap around. But we could also not do that because it doesn't really matter!
                var end = Math.Min(Tree.nodeCount, startIndex + PerWorkerCacheOptimizeCount);
                for (int i = startIndex; i < end; ++i)
                {
                    Tree.IncrementalCacheOptimizeThreadSafe(i);
                }

            }
        }

        unsafe void CheckForRefinementOverlaps(int nodeIndex, ref QuickList<int, Buffer<int>> refinementTargets)
        {
            var node = nodes + nodeIndex;
            var children = &node->A;
            for (int childIndex = 0; childIndex < 2; ++childIndex)
            {
                ref var child = ref children[childIndex];
                if (child.Index >= 0)
                {
                    for (int i = 0; i < refinementTargets.Count; ++i)
                    {
                        if (refinementTargets[i] == child.Index)
                            Console.WriteLine("Found a refinement target in the children of a refinement target.");
                    }

                    CheckForRefinementOverlaps(child.Index, ref refinementTargets);
                }

            }
        }

    }
}
