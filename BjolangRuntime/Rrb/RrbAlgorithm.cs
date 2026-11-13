using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Bjolang.Runtime.Rrb;

[NonUserCode]
internal static class RrbAlgorithm
{
    public static Node<T> Update<T>(Node<T> root, int index, T value, int shift, OwnerId token)
    {
        // Tree is not a tree.
        if (shift == 0)
        {
            var leaf = (LeafNode<T>)root;

            // Direct CloneAndSet for persistent
            if (token.IsNone)
                return leaf.CloneAndSet(index & Constants.RRB_MASK, value);

            // Transient path
            leaf = leaf.EnsureEditable(token);
            leaf.Items[index & Constants.RRB_MASK] = value;
            return leaf;
        }

        var internalNode = (InternalNode<T>)root;

        var (childIndex, subIndex) = GetChildIndexAvx(internalNode, index, shift);

        if (childIndex >= internalNode.Len) throw new IndexOutOfRangeException();

        var child = internalNode.Children[childIndex]!;
        var newChild = Update(child, subIndex, value, shift - Constants.RRB_BITS, token);

        if (token.IsNone)
            // Cone your way back up
            return internalNode.CloneAndSetChild(childIndex, newChild);

        internalNode = internalNode.EnsureEditable(token);
        internalNode.Children[childIndex] = newChild;
        return internalNode;
    }
    
        // This is the hairy part compared to clojure's balanced tries. 
    // It alculates a redistribution plan for a set of nodes to restore the RRB tree invariants.
    // This method implements the "Search and Redistribute" algorithm found in the original C implementation.
    // It iterates through the list of children and identifies nodes that are underfilled. This is defined
    // by the constants RRB_BRANCHING - RRB_INVARIANT.
    //  
    // When an underfilled node is found, it "steals" items from the subsequent node to fill the current 
    // node up to capacity. This "Sliding Window" approach ensures that the resulting nodes are densely 
    // packed, preventing the tree from becoming sparse and deep after multiple concatenations.
    //
    // Returns An array of integers representing the target size (len) for each new node. 
    // For example, if the plan is [32, 32, 5],ExecuteConcatPlan will 
    // create three nodes containing 32, 32, and 5 items respectively.

    // The most children a node may have when they hold `slots` between them:
    // the fewest that could hold them, plus RRB_EXTRAS. This is the search step
    // invariant, and the bound a concatenation plan restores.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AllowedChildren(int slots) =>
        (slots - 1) / Constants.RRB_BRANCHING + 1 + Constants.RRB_EXTRAS;

    public static (Node<T> Node, int Size) Concat<T>(Node<T> leftNode, int leftSize, Node<T> rightNode, int rightSize, int leftShift, int rightShift, out int newShift)
    {
        if (leftShift > rightShift)
        {
            var left = AsInternal(leftNode);
            var lastChild = left.Children[left.Len - 1]!;
            var lastChildSize = GetChildKnownSize(left, left.Len - 1, leftSize, leftShift);
            
            var (mergedMid, mergedSize) = Concat(lastChild, lastChildSize, rightNode, rightSize, leftShift - Constants.RRB_BITS, rightShift, out var subShift);
            return Rebalance(left, leftSize - lastChildSize, mergedMid, mergedSize, null, 0, leftShift, subShift, out newShift);
        }

        if (leftShift < rightShift)
        {
            var right = AsInternal(rightNode);
            var firstChild = right.Children[0]!;
            var firstChildSize = GetChildKnownSize(right, 0, rightSize, rightShift);

            var (mergedMid, mergedSize) = Concat(leftNode, leftSize, firstChild, firstChildSize, leftShift, rightShift - Constants.RRB_BITS, out var subShift);
            return Rebalance(null, 0, mergedMid, mergedSize, right, rightSize - firstChildSize, rightShift, subShift, out newShift);
        }

        // Two leaves whose items fit in one become one. Two internal nodes go
        // through the seam below even when their children would fit in one:
        // joining the lists as they are leaves the nodes at the seam as they
        // were, and a part-full tail that Merge pushed into the left tree is
        // one of them. Rebalance joins them as they are when that keeps the
        // invariant.
        if (leftShift == 0 && leftNode.Len + rightNode.Len <= Constants.RRB_BRANCHING)
        {
            newShift = 0;
            var newLen = leftNode.Len + rightNode.Len;
            var lLeaf = AsLeaf(leftNode);
            var rLeaf = AsLeaf(rightNode);
            var newItems = new T[newLen];
            Array.Copy(lLeaf.Items, 0, newItems, 0, lLeaf.Len);
            Array.Copy(rLeaf.Items, 0, newItems, lLeaf.Len, rLeaf.Len);
            return (new LeafNode<T>(newItems, newLen, OwnerId.None), leftSize + rightSize);
        }

        if (leftShift == 0)
        {
            newShift = Constants.RRB_BITS;
            var children = new Node<T>?[2] { leftNode, rightNode };
            var sizes = new int[2] { leftSize, leftSize + rightSize };
            return (new InternalNode<T>(children, sizes, 2, OwnerId.None), leftSize + rightSize);
        }

        var leftI = AsInternal(leftNode);
        var rightI = AsInternal(rightNode);
        var midLeft = leftI.Children[leftI.Len - 1]!;
        var midLeftSize = GetChildKnownSize(leftI, leftI.Len - 1, leftSize, leftShift);
        var midRight = rightI.Children[0]!;
        var midRightSize = GetChildKnownSize(rightI, 0, rightSize, rightShift);

        var (mergedMid2, mergedSize2) = Concat(midLeft, midLeftSize, midRight, midRightSize, leftShift - Constants.RRB_BITS, rightShift - Constants.RRB_BITS, out var subShift2);
        return Rebalance(leftI, leftSize - midLeftSize, mergedMid2, mergedSize2, rightI, rightSize - midRightSize, leftShift, subShift2, out newShift);
    }

    private static (Node<T> Node, int Size) Rebalance<T>(
        InternalNode<T>? left, int leftBaseSize,
        Node<T> center, int centerSize,
        InternalNode<T>? right, int rightBaseSize,
        int shift, int centerShift, out int newShift)
    {
        int totalSize = leftBaseSize + centerSize + rightBaseSize;
        int childCount = (left != null ? left.Len - 1 : 0) + (centerShift == shift ? AsInternal(center).Len : 1) + (right != null ? right.Len - 1 : 0);

        // What the children hold between them, for the search step invariant.
        // Children that fit in one node are kept as they are only when they
        // keep it; otherwise a run of small merges piles part-full children
        // into one node until lookups in it have to search far.
        // Above the leaves a slot is an item, and how many is already known.
        var slots = 0;
        if (shift == Constants.RRB_BITS)
        {
            slots = totalSize;
        }
        else
        {
            if (left != null)
                for (var i = 0; i < left.Len - 1; i++) slots += left.Children[i]!.Len;
            if (centerShift == shift)
            {
                var c = AsInternal(center);
                for (var i = 0; i < c.Len; i++) slots += c.Children[i]!.Len;
            }
            else
            {
                slots += center.Len;
            }
            if (right != null)
                for (var i = 1; i < right.Len; i++) slots += right.Children[i]!.Len;
        }

        if (childCount <= Constants.RRB_BRANCHING && childCount <= AllowedChildren(slots))
        {
            var newChildren = new Node<T>?[childCount];
            var newSizes = new int[childCount];
            int idx = 0, cumulative = 0;

            if (left != null)
            {
                for (int i = 0; i < left.Len - 1; i++)
                {
                    newChildren[idx] = left.Children[i];
                    cumulative += GetChildKnownSize(left, i, leftBaseSize + GetChildKnownSize(left, left.Len - 1, int.MaxValue, shift), shift);
                    newSizes[idx++] = cumulative;
                }
            }
            if (centerShift == shift)
            {
                var cInt = AsInternal(center);
                for (int i = 0; i < cInt.Len; i++)
                {
                    newChildren[idx] = cInt.Children[i];
                    cumulative += GetChildKnownSize(cInt, i, centerSize, shift);
                    newSizes[idx++] = cumulative;
                }
            }
            else
            {
                newChildren[idx] = center;
                cumulative += centerSize;
                newSizes[idx++] = cumulative;
            }
            if (right != null)
            {
                for (int i = 1; i < right.Len; i++)
                {
                    newChildren[idx] = right.Children[i];
                    cumulative += GetChildKnownSize(right, i, rightBaseSize + GetChildKnownSize(right, 0, int.MaxValue, shift), shift);
                    newSizes[idx++] = cumulative;
                }
            }

            newShift = shift;
            return (new InternalNode<T>(newChildren, newSizes, childCount, OwnerId.None), totalSize);
        }

        // Fallback to redistribution
        var allChildren = new Node<T>[childCount];
        Span<int> allSizes = stackalloc int[childCount];
        int count = 0;

        if (left != null)
        {
            for (var i = 0; i < left.Len - 1; i++)
            {
                allChildren[count] = left.Children[i]!;
                allSizes[count++] = GetChildKnownSize(left, i, leftBaseSize + GetChildKnownSize(left, left.Len - 1, int.MaxValue, shift), shift);
            }
        }
        if (centerShift == shift)
        {
            var cInternal = AsInternal(center);
            for (var i = 0; i < cInternal.Len; i++)
            {
                allChildren[count] = cInternal.Children[i]!;
                allSizes[count++] = GetChildKnownSize(cInternal, i, centerSize, shift);
            }
        }
        else
        {
            allChildren[count] = center;
            allSizes[count++] = centerSize;
        }
        if (right != null)
        {
            for (var i = 1; i < right.Len; i++)
            {
                allChildren[count] = right.Children[i]!;
                allSizes[count++] = GetChildKnownSize(right, i, rightBaseSize + GetChildKnownSize(right, 0, int.MaxValue, shift), shift);
            }
        }

        Span<int> plan = stackalloc int[count];
        CreateConcatPlan(new ReadOnlySpan<Node<T>>(allChildren, 0, count), plan, out var topLen);

        var newAll = ExecuteConcatPlan(new ReadOnlySpan<Node<T>>(allChildren, 0, count), allSizes[..count], plan, topLen, shift);

        if (topLen <= Constants.RRB_BRANCHING)
        {
            newShift = shift;
            return (newAll, totalSize);
        }

        var newLeft = CopyInternalAndSetSizes(newAll, 0, Constants.RRB_BRANCHING);
        var newRight = CopyInternalAndSetSizes(newAll, Constants.RRB_BRANCHING, topLen - Constants.RRB_BRANCHING);

        newShift = shift + Constants.RRB_BITS;
        var parent = new InternalNode<T>(new Node<T>?[2] { newLeft.Node, newRight.Node }, new int[2] { newLeft.Size, totalSize }, 2, OwnerId.None);
        return (parent, totalSize);
    }

    private static void CreateConcatPlan<T>(ReadOnlySpan<Node<T>> allChildren, Span<int> nodeCount, out int topLen)
    {
        long totalNodes = 0;
        for (var i = 0; i < allChildren.Length; i++)
        {
            var len = allChildren[i].Len;
            nodeCount[i] = len;
            totalNodes += len;
        }

        var optimalSlots = (totalNodes - 1) / Constants.RRB_BRANCHING + 1;
        var shuffledLen = allChildren.Length;
        var iIdx = 0;

        while (optimalSlots + Constants.RRB_EXTRAS < shuffledLen)
        {
            while (iIdx < shuffledLen && nodeCount[iIdx] > Constants.RRB_BRANCHING - Constants.RRB_INVARIANT) iIdx++;
            if (iIdx == shuffledLen) break;

            var remainingNodes = nodeCount[iIdx];
            do
            {
                if (iIdx + 1 >= shuffledLen) break;
                var minSize = Math.Min(remainingNodes + nodeCount[iIdx + 1], Constants.RRB_BRANCHING);
                nodeCount[iIdx] = minSize;
                remainingNodes = remainingNodes + nodeCount[iIdx + 1] - minSize;
                iIdx++;
            } while (remainingNodes > 0);

            for (var j = iIdx; j < shuffledLen - 1; j++) nodeCount[j] = nodeCount[j + 1];
            shuffledLen--;
            iIdx--;
        }
        topLen = shuffledLen;
    }
internal static InternalNode<T> ExecuteConcatPlan<T>(ReadOnlySpan<Node<T>> all, ReadOnlySpan<int> sizes, Span<int> plan, int slen, int shift)
    {
        var newChildren = new Node<T>?[slen];
        var newSizes = new int[slen];
        var idx = 0;
        var offset = 0;
        int runningTotalSize = 0;

        var shufflingLeaves = shift == Constants.RRB_BITS;

        for (var i = 0; i < slen; i++)
        {
            var newSize = plan[i];
            if (offset == 0 && idx < all.Length && all[idx].Len == newSize)
            {
                newChildren[i] = all[idx];
                runningTotalSize += sizes[idx];
                newSizes[i] = runningTotalSize;
                idx++;
                continue;
            }

            int nodeAccumulatedSize = 0;
            if (shufflingLeaves)
            {
                var newItems = new T[newSize];
                var curSize = 0;
                while (curSize < newSize)
                {
                    var srcLeaf = AsLeaf(all[idx]);
                    var available = srcLeaf.Len - offset;
                    var toCopy = Math.Min(available, newSize - curSize);
                    Array.Copy(srcLeaf.Items, offset, newItems, curSize, toCopy);
                    curSize += toCopy;
                    offset += toCopy;
                    nodeAccumulatedSize += toCopy; // This is fine because toCopy is a flat count, not cumulative.
                    if (offset == srcLeaf.Len) { idx++; offset = 0; }
                }
                newChildren[i] = new LeafNode<T>(newItems, newSize, OwnerId.None);
            }
            else
            {
                var newSubChildren = new Node<T>?[newSize];
                var newSubSizes = new int[newSize];
                var curSize = 0;
                int subTotal = 0; // Cumulative for the new node
                while (curSize < newSize)
                {
                    var srcInternal = AsInternal(all[idx]);
                    var available = srcInternal.Len - offset;
                    var toCopy = Math.Min(available, newSize - curSize);
                    
                    for(int c = 0; c < toCopy; c++) {
                        int childIdx = offset + c;
                        int childSz = GetChildKnownSize(srcInternal, childIdx, sizes[idx], shift - Constants.RRB_BITS);
                        subTotal += childSz;
                        newSubSizes[curSize + c] = subTotal;
                    }

                    Array.Copy(srcInternal.Children, offset, newSubChildren, curSize, toCopy);
                    curSize += toCopy;
                    offset += toCopy;
                    
                    // REMOVED: nodeAccumulatedSize += subTotal;
                    
                    if (offset == srcInternal.Len) { idx++; offset = 0; }
                }
                newChildren[i] = new InternalNode<T>(newSubChildren, newSubSizes, newSize, OwnerId.None);

                // The node is made of whole children moved from other nodes, so
                // it can hold more part-full ones than the invariant allows, and
                // a run of merges would pile them up. It is repacked when it
                // does. Above the leaves a slot is an item.
                var childShift = shift - Constants.RRB_BITS;
                var slots = childShift == Constants.RRB_BITS ? subTotal : SlotsOfChildren(newSubChildren, newSize);
                if (newSize > AllowedChildren(slots))
                {
                    var subSizes = new int[newSize];
                    for (var c = 0; c < newSize; c++) subSizes[c] = newSubSizes[c] - (c > 0 ? newSubSizes[c - 1] : 0);
                    newChildren[i] = BuildChecked(newSubChildren, subSizes, newSize, childShift).NewNode;
                }

                // The total size of this newly constructed node is exactly subTotal.
                nodeAccumulatedSize = subTotal;
            }
            
            runningTotalSize += nodeAccumulatedSize;
            newSizes[i] = runningTotalSize;
        }
        return new InternalNode<T>(newChildren, newSizes, slen, OwnerId.None);
    }

    private static (InternalNode<T> Node, int Size) CopyInternalAndSetSizes<T>(InternalNode<T> orig, int start, int len)
    {
        var newArr = new Node<T>?[len];
        var newSizes = new int[len];
        int baseSub = start > 0 ? orig.SizeTable![start - 1] : 0;
        
        Array.Copy(orig.Children, start, newArr, 0, len);
        for (int i = 0; i < len; i++)
        {
            newSizes[i] = orig.SizeTable![start + i] - baseSub;
        }
        return (new InternalNode<T>(newArr, newSizes, len, OwnerId.None), newSizes[len - 1]);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetChildKnownSize<T>(InternalNode<T> node, int childIndex, int knownSize, int shift)
    {
        if (node.SizeTable != null)
        {
            int prev = childIndex > 0 ? node.SizeTable[childIndex - 1] : 0;
            return node.SizeTable[childIndex] - prev;
        }
        if (childIndex < node.Len - 1) return 1 << shift;
        return knownSize - (node.Len - 1) * (1 << shift);
    }

  private static InternalNode<T> SetSizes<T>(InternalNode<T> node, int shift)
    {
        var childShift = shift - Constants.RRB_BITS;
        var expectedBlockSize = 1 << shift;
        var isBalanced = true;
  
        // Scan Phase: Detect if we violate Dense Invariants
        // We MUST verify sizes because Rebalance() might have put a partial node in the middle.
        for (var i = 0; i < node.Len; i++)
        {
            var child = node.Children[i]!;
  
            // If child is Relaxed, we must be Relaxed.
            if (child.IsRelaxed())
            {
                isBalanced = false;
                break;
            }
  
            // B. If child is Middle (not last), it MUST be full.
            if (i < node.Len - 1)
            {
                // For Leaf Nodes (Shift 5 parent -> Shift 0 child), 
                // reading .Len is O(1). This avoids the CountTree overhead for the most common case.
                int size;
                if (childShift == 0)
                {
                    // Unsafe cast is safe here because of shift logic
                    size = AsLeaf(child).Len;
                }
                else
                {
                    // For Internal nodes, we must traverse the spine to be sure.
                    // This is O(Height), which is acceptable (max 5-6 steps).
                    size = CountTree(child, childShift);
                }
  
                if (size != expectedBlockSize)
                {
                    isBalanced = false;
                    break;
                }
            }
            // Note: We don't check the last child. 
            // A Dense node is allowed to have a partial last child.
        }
  
        // Fast Path: It's a valid Dense node. 
        if (isBalanced)
            return new InternalNode<T>(node.Children, null, node.Len, OwnerId.None);
  
  
        // 2. Build Path: We are Relaxed. Build the table.
        // We repeat the size logic here, but we can't share state easily without allocating.
        // Since we only hit this path when necessary, the double-check is worth the memory savings on the Fast Path.
        var sizes = new int[node.Len];
        var sum = 0;
  
        for (var i = 0; i < node.Len; i++)
        {
            var child = node.Children[i]!;
            int size;
  
            if (child.IsRelaxed())
            {
                // Trust the existing table (O(1))
                var internalChild = AsInternal(child);
                size = internalChild.SizeTable![internalChild.Len - 1];
            }
            else if (childShift == 0)
            {
                // Leaf Optimization (O(1))
                size = AsLeaf(child).Len;
            }
            else
            {
                // Dense Internal traversal (O(Height))
                size = CountTree(child, childShift);
            }
  
            sum += size;
            sizes[i] = sum;
        }
  
        return new InternalNode<T>(node.Children, sizes, node.Len, OwnerId.None);
    }
 
  
    internal static int CountTree<T>(Node<T> node, int shift)
    {
        var totalSize = 0;
        
        // Fast Path: Relaxed Node
        // If the node has a SizeTable, we can stop immediately. The table holds the accurate total count.
        if (node.IsRelaxed())
            return AsInternal(node).SizeTable![node.Len - 1];

        // Iterate down the rightmost edge until we hit a leaf or a relaxed node
        while (shift > 0)
        {
            var internalNode = AsInternal(node);
            // We know that in a dense node, all children except the last one are fully populated.
            // We calculate the size of the full siblings and accumulate it.
            // Math: (ChildCount - 1) * CapacityOfOneChild
            totalSize += (internalNode.Len - 1) * (1 << shift);

            // Move down to the last child to continue counting
            node = internalNode.Children[internalNode.Len - 1]!;
            shift -= Constants.RRB_BITS;
        }

        // Base Case: Leaf Node
        // We add the actual number of elements in the final leaf.
        return totalSize + node.Len;
    }

    private static InternalNode<T> CopyInternal<T>(InternalNode<T> orig, int start, int len)
    {
        var newArr = new Node<T>?[len];
        Array.Copy(orig.Children, start, newArr, 0, len);
        return new InternalNode<T>(newArr, null, len, OwnerId.None);
    }
    
     internal static Node<T> SliceLeftRec<T>(Node<T> node, int toDrop, int shift)
    {
        // Base Case: Leaf
        if (shift == 0)
        {
            var leaf = AsLeaf(node);
            var newLen = leaf.Len - toDrop;
            var newItems = new T[newLen];
            Array.Copy(leaf.Items, toDrop, newItems, 0, newLen);
            return new LeafNode<T>(newItems, newLen, OwnerId.None);
        }

        var internalNode = AsInternal(node);
        var (subidx, dropInChild) = GetChildIndexAvx(internalNode, toDrop, shift);


        // Reconstruct Children Array
        // We discard children [0...subidx-1]
        // We slice child [subidx]
        // We keep children [subidx+1...end]

        var remainingChildren = internalNode.Len - subidx;
        var newChildren = new Node<T>?[remainingChildren];

        // Handle the split child
        // if dropInChild == 0, we keep the whole child.
        if (dropInChild > 0)
            newChildren[0] = SliceLeftRec(internalNode.Children[subidx]!, dropInChild, shift - Constants.RRB_BITS);
        else
            newChildren[0] = internalNode.Children[subidx];

        // Copy remaining siblings
        if (remainingChildren > 1) Array.Copy(internalNode.Children, subidx + 1, newChildren, 1, remainingChildren - 1);

        // Rebuild Size Table
        // If we slice from the left, indices shift, so we almost always need a SizeTable.
        // Exception: If we dropped exact whole subtrees from a balanced node, it stays balanced!
        var staysBalanced = internalNode.IsDense() && dropInChild == 0;

        int[]? newSizeTable = null;
        if (!staysBalanced)
        {
            newSizeTable = new int[remainingChildren];

            if (internalNode.SizeTable != null)
            {
                // Adjust existing table
                for (var i = 0; i < remainingChildren; i++)
                    newSizeTable[i] = internalNode.SizeTable[subidx + i] - toDrop;
            }
            else
            {
                // Create table from balanced assumptions
                var childCapacity = 1 << shift;

                for (var i = 0; i < remainingChildren; i++)
                {
                    long oldCumulative;

                    // Check if we are at the very last child of the ORIGINAL node.
                    // The index in the original internalNode.Children is (subidx + i).
                    if (subidx + i == internalNode.Len - 1)
                    {
                        // The last child of a dense node is the ONLY one that might not be full.
                        // We must calculate the actual total size of the original node.

                        // 1. Sum of all preceding full siblings
                        var fullChildrenSize = (long)(internalNode.Len - 1) * childCapacity;

                        // 2. Actual size of the last child (use CountTree to traverse down)
                        var lastChildSize = CountTree(internalNode.Children[internalNode.Len - 1]!,
                            shift - Constants.RRB_BITS);

                        oldCumulative = fullChildrenSize + lastChildSize;
                    }
                    else
                    {
                        // Any child that is NOT the last child in a dense node is guaranteed full.
                        oldCumulative = (long)(subidx + i + 1) * childCapacity;
                    }

                    newSizeTable[i] = (int)(oldCumulative - toDrop);
                }
            }
        }

        return new InternalNode<T>(newChildren, newSizeTable, remainingChildren, OwnerId.None);
    }

    /// <summary>
    ///     Slices the tree at 'limit'.
    ///     If the resulting rightmost leaf is Partial (less than 32), it is detached and returned as PromotedTail.
    ///     If the resulting rightmost leaf is Full (32), it stays in the tree (PromotedTail is Empty).
    /// </summary>
    internal static Node<T>? SliceRightAndPromote<T>(Node<T> node, int limit, int shift, out T[] promotedTail, out int tailLen)
{
    // --- Base Case: Leaf ---
    if (shift == 0)
    {
        var leaf = AsLeaf(node);

        // Optimization: If taking the exact existing leaf
        if (leaf.Len == limit)
        {
            if (limit < Constants.RRB_BRANCHING)
            {
                // It's partial -> Promote it
                promotedTail = leaf.Items;
                tailLen = leaf.Len;
                return null;
            }
            else
            {
                // It's full -> Keep it
                promotedTail = []; // Or Array.Empty<T>()
                tailLen = 0;
                return leaf;
            }
        }

        // Otherwise, Create the slice
        var newItems = new T[limit];
        Array.Copy(leaf.Items, 0, newItems, 0, limit);

        if (limit < Constants.RRB_BRANCHING)
        {
            promotedTail = newItems;
            tailLen = limit;
            return null;
        }
        else
        {
            promotedTail = [];
            tailLen = 0;
            return new LeafNode<T>(newItems, limit, OwnerId.None);
        }
    }

    // --- Recursive Step: Internal ---
    var internalNode = AsInternal(node);

    // Find which child contains the 'limit' boundary
    var (childIdx, idxInChild) = GetChildIndexAvx(internalNode, limit - 1, shift);

    // Recurse strictly on that child
    // The recursive call writes directly to our out parameters
    var limitInChild = idxInChild + 1;
    var childResult = SliceRightAndPromote(internalNode.Children[childIdx]!, limitInChild, shift - Constants.RRB_BITS, out promotedTail, out tailLen);

    // --- Reconstruct the Node ---

    // 1. Determine new length of this node
    // If childResult is null, it means the child was fully consumed (promoted).
    var newLen = childResult == null ? childIdx : childIdx + 1;

    if (newLen == 0)
    {
        // If we lost the only child, this node disappears too.
        // promotedTail and tailLen are already set by the recursive call.
        return null;
    }

    // 2. Allocate and Copy Children
    var newChildren = new Node<T>?[newLen];

    // Copy preceding children (they are untouched)
    if (childIdx > 0)
        Array.Copy(internalNode.Children, 0, newChildren, 0, Math.Min(childIdx, newLen));

    // If the child was modified (not removed), place it
    if (childResult != null)
        newChildren[childIdx] = childResult;

    // 3. Handle SizeTable (Preserve Density Optimization)
    // If the node was Dense, slicing from the right preserves density (remaining children are still full),
    // so we only need a SizeTable if the original node was Relaxed.
    int[]? newSizeTable = null;
    
    if (internalNode.SizeTable != null)
    {
        newSizeTable = new int[newLen];
        Array.Copy(internalNode.SizeTable, newSizeTable, newLen);

        // Update the last entry to reflect the exact cut
        // The size at this boundary is exactly 'limit', MINUS whatever we promoted.
        // We use the tailLen output from the recursive call here.
        newSizeTable[newLen - 1] = limit - tailLen;
    }

    return new InternalNode<T>(newChildren, newSizeTable, newLen, OwnerId.None);
}

// Returns the updated node if the tail could be inserted/merged.
// Returns NULL if the node is physically full and the tail could not be accepted.
    private static Node<T>? TryPushDownTail<T>(Node<T> node, LeafNode<T> tail, int shift, OwnerId token)
    {
        var internalNode = AsInternal(node);

        // 1. Leaf Parent Level (Shift 5) -> Append directly
        if (shift == Constants.RRB_BITS)
        {
            if (internalNode.Len < Constants.RRB_BRANCHING)
                return AppendChild(internalNode, tail, shift, token);
            return null;
        }

        // 2. Internal Level -> Recurse Spine
        if (internalNode.Len > 0)
        {
            var lastIdx = internalNode.Len - 1;
            var lastChild = internalNode.Children[lastIdx]!;

            var newLastChild = TryPushDownTail(lastChild, tail, shift - Constants.RRB_BITS, token);

            if (newLastChild != null)
            {
                // Update the pointer in the current node
                var editable = internalNode.EnsureEditable(token);
                editable.Children[lastIdx] = newLastChild;

                // Update metadata (SizeTable) ONLY if necessary
                if (editable.IsRelaxed())
                    editable.SizeTable![lastIdx] += tail.Len;
                else if (newLastChild.IsRelaxed())
                    // A relaxed child needs a relaxed parent. The table is
                    // built from the updated child.
                    editable = CreateRelaxedNodeFromDense(editable, token, shift);

                return editable;
            }
        }

        // 3. Append New Path (Sibling to existing spine)
        if (internalNode.Len < Constants.RRB_BRANCHING)
        {
            var newPath = CreatePath(shift - Constants.RRB_BITS, tail, token);
            return AppendChild(internalNode, newPath, shift, token);
        }

        return null;
    }

    private static InternalNode<T> AppendChild<T>(InternalNode<T> node, Node<T> childToAdd, int shift, OwnerId token)
    {
        // Check if appending this child violates the Dense Invariant.
        // Violation happens if we are currently Dense, but the LAST child is not full.
        var requiresRelaxation = node.IsDense() && childToAdd.IsRelaxed();

        if (!requiresRelaxation && node.IsDense() && node.Len > 0)
        {
            // We only strictly need to check this at the leaf-parent level (Shift 5)
            // or if we trust that higher levels handle their own density.
            if (shift == Constants.RRB_BITS)
            {
                var lastChild = AsLeaf(node.Children[node.Len - 1]!);
                if (lastChild.Len < Constants.RRB_BRANCHING)
                    requiresRelaxation = true;
            }
            else
            {
                // For higher levels, if we are Dense, the last child might be a 
                // Dense node that is physically full (Len 32) but structurally partial.
                // However, checking the physical size is usually enough here:
                var lastChild = node.Children[node.Len - 1]!;
                var totalSize = CountTree(lastChild, shift - Constants.RRB_BITS);
                if (totalSize < 1 << shift)
                    requiresRelaxation = true;
            }
        }

        // 1. Get Writable Node
        // If we detected a violation, we MUST force expansion to include a SizeTable.

        InternalNode<T> editable = requiresRelaxation 
            ? CreateRelaxedNodeFromDense(node, token, shift) 
            : node.EnsureEditable(token, true);

        // 2. Insert
        editable.Children[editable.Len] = childToAdd;

        // 3. Update SizeTable (It exists if we were already relaxed OR if we just forced it)
        if (editable.IsRelaxed())
        {
            var prevTotal = editable.Len > 0 ? editable.SizeTable![editable.Len - 1] : 0;

            var addedSize = shift == Constants.RRB_BITS
                ? AsLeaf(childToAdd).Len
                : CountTree(childToAdd, shift - Constants.RRB_BITS);

            editable.SizeTable![editable.Len] = prevTotal + addedSize;
        }

        editable.Len++;
        return editable;
    }


    internal static InternalNode<T> CreateRelaxedNodeFromDense<T>(InternalNode<T> node, OwnerId token, int shift)
    {
        // 1. Determine Capacity
        // If we have an Owner (Transient), we go full capacity (32).
        // If Persistent, we allocate exactly existing Len + 1 (room for the new child).
        var newCap = !token.IsNone ? Constants.RRB_BRANCHING : node.Len + 1;

        var newChildren = new Node<T>?[newCap];
        var newTable = new int[newCap];

        Array.Copy(node.Children, newChildren, node.Len);

        // 2. Build the Size Table
        // Since 'node' was Dense, we know all children 0 to Len-2 are FULL.
        // Only node.Children[Len-1] might be partial.

        var blockSize = 1 << shift;
        var currentSum = 0;
        var childShift = shift - Constants.RRB_BITS;

        for (var i = 0; i < node.Len; i++)
        {
            if (i < node.Len - 1)
                // Guaranteed full
                currentSum += blockSize;
            else
                // Last child: Calculate actual size
                // (We can assume the last child is Dense because the parent was Dense)
                currentSum += CountTree(node.Children[i]!, childShift);
            // Or use RrbAlgorithm.CountTree which handles the flags check
            newTable[i] = currentSum;
        }

        // 3. Return new node marked as Relaxed
        var newNode = new InternalNode<T>(newChildren, newTable, node.Len, token);
        // Note: InternalNode ctor sets IsRelaxed if table != null
        return newNode;
    }




    public static Node<T> AppendLeafToTree<T>(Node<T>? root, LeafNode<T> leafToPush, ref int shift, OwnerId token)
    {
        if (root == null)
        {
            shift = 0;
            return leafToPush;
        }

        // Special Case: Root is a leaf (Shift 0) -> Turn into tree (Shift 5)
        if (shift == 0)
        {
            shift = Constants.RRB_BITS;
            return CreateNewParent(root, leafToPush, token);
        }

        var newRoot = TryPushDownTail(root, leafToPush, shift, token);

        if (newRoot != null) return newRoot;

        // Everything from here is Root overflow.
        // at this point, we know that root is an InternalNode due to TryPushDownTail failing.
        var oldRootInode = AsInternal(root);


        // We get the size of the old root.
        var rootTotalSize = oldRootInode.SizeTable != null
            ? oldRootInode.SizeTable[oldRootInode.Len - 1]
            : CountTree(oldRootInode, shift); // Use optimized count if dense

        // Calculate theoretical capacity of a full node at this level
        //    (e.g., if shift is 5, a full node holds 32 * 32 = 1024 items)
        var rootCapacity = 1 << (shift + Constants.RRB_BITS);

        // Now the stinky stuff:
        // Determine if we need a SizeTable for the new parent
        int[]? newSizeTable = null;

        // We need a table if the old root was explicitly relaxed OR 
        // if it simply wasn't fully populated (like if we pushed a tail when
        // the rightmost node wasn't full. 
        if (oldRootInode.SizeTable != null || rootTotalSize != rootCapacity)
        {
            newSizeTable = new int[!token.IsNone ? Constants.RRB_BRANCHING : 2];
            newSizeTable[0] = rootTotalSize;
            newSizeTable[1] = rootTotalSize + leafToPush.Len;
        }

        //Create the new Parent
        var newChildren = new Node<T>?[!token.IsNone ? Constants.RRB_BRANCHING : 2];
        newChildren[0] = root;
        newChildren[1] = CreatePath(shift, leafToPush, token);

        var newParent = new InternalNode<T>(newChildren, newSizeTable, 2, token);

        // Update shift for the caller
        shift += Constants.RRB_BITS;

        return newParent;
    }

    // ---------------------------------------------------------------------
    // The left edge: the head's counterparts of AppendLeafToTree and
    // SliceRightAndPromote.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Puts a full leaf in front of the tree, the mirror of AppendLeafToTree.
    ///     Returns null when doing so would break the search step invariant at
    ///     some node, and the caller then goes through Concat, which repacks.
    /// </summary>
    /// <remarks>
    ///     Only full leaves come here. A full leaf in front of the children of
    ///     a dense node above the leaves leaves it dense, as every child but the
    ///     last is still full. Further up, the first child grows, and a dense
    ///     node with more than that child has to become relaxed.
    /// </remarks>
    internal static Node<T>? TryPrependLeaf<T>(Node<T>? root, LeafNode<T> leaf, ref int shift)
    {
        if (root == null)
        {
            shift = 0;
            return leaf;
        }

        // A lone leaf for root: the new leaf is full, so the parent is dense.
        if (shift == 0)
        {
            shift = Constants.RRB_BITS;
            return CreateNewParent(leaf, root, OwnerId.None);
        }

        var failed = false;
        var newRoot = PrependDown(root, leaf, shift, ref failed);
        if (failed) return null;
        if (newRoot != null) return newRoot;

        // The whole left spine is full: a new root over a path to the leaf and
        // the old root. The path holds less than a node at this level, so
        // the root is relaxed.
        var rootSize = CountTree(root, shift);
        var children = new Node<T>?[] { CreatePath(shift, leaf, OwnerId.None), root };
        var table = new[] { leaf.Len, leaf.Len + rootSize };
        shift += Constants.RRB_BITS;
        return new InternalNode<T>(children, table, 2, OwnerId.None);
    }

    // The node with `leaf` put in front of its first leaf, or null when the
    // left spine below it is full. Sets `failed` when there was room, but
    // using it would break the search step invariant.
    private static InternalNode<T>? PrependDown<T>(Node<T> node, LeafNode<T> leaf, int shift, ref bool failed)
    {
        var inode = AsInternal(node);
        var len = inode.Len;
        var added = leaf.Len;

        // Above the leaves: the leaf becomes the first child.
        if (shift == Constants.RRB_BITS)
        {
            if (len >= Constants.RRB_BRANCHING) return null;
            return PrependChild(inode, leaf, added, shift, keepsDense: inode.IsDense());
        }

        var first = inode.Children[0]!;
        var newFirst = PrependDown(first, leaf, shift - Constants.RRB_BITS, ref failed);
        if (failed) return null;

        if (newFirst != null)
        {
            var children = new Node<T>?[len];
            Array.Copy(inode.Children, children, len);
            children[0] = newFirst;

            // A dense node with one child stays dense if that child does: the
            // child is also its last, which may be part-full.
            int[]? table = null;
            if (inode.IsRelaxed() || len > 1 || newFirst.IsRelaxed())
            {
                table = inode.SizeTable != null ? (int[])inode.SizeTable.Clone() : DenseTable(inode, shift);
                for (var i = 0; i < len; i++) table[i] += added;
            }

            // The first child has as many or one more children than it had,
            // so the slots grew and the invariant cannot have moved.
            return new InternalNode<T>(children, table, len, OwnerId.None);
        }

        // The spine below is full: a new path to the leaf in front.
        if (len >= Constants.RRB_BRANCHING) return null;

        // One more child holding one slot.
        var slots = SlotsOfChildren(inode.Children, len) + 1;
        if (len + 1 > AllowedChildren(slots))
        {
            failed = true;
            return null;
        }

        var path = CreatePath(shift - Constants.RRB_BITS, leaf, OwnerId.None);
        return PrependChild(inode, path, added, shift, keepsDense: false);
    }

    // `node` with `child`, which holds `childSize` items, in front of its children.
    private static InternalNode<T> PrependChild<T>(InternalNode<T> node, Node<T> child, int childSize, int shift, bool keepsDense)
    {
        var len = node.Len;
        var children = new Node<T>?[len + 1];
        children[0] = child;
        Array.Copy(node.Children, 0, children, 1, len);

        int[]? table = null;
        if (!keepsDense)
        {
            var old = node.SizeTable ?? DenseTable(node, shift);
            table = new int[len + 1];
            table[0] = childSize;
            for (var i = 0; i < len; i++) table[i + 1] = old[i] + childSize;
        }

        return new InternalNode<T>(children, table, len + 1, OwnerId.None);
    }

    /// <summary>
    ///     The tree without its first leaf, which is handed back in
    ///     <paramref name="leaf" />: the mirror of SliceRightAndPromote, for a
    ///     head that has run out. Null when the leaf was all the tree had. The
    ///     root may be left taller than it need be; see LowerRoot.
    /// </summary>
    /// <remarks>
    ///     A dense node that loses its first child stays dense: the rest are
    ///     still full but for the last. A dense node whose first child shrinks
    ///     becomes relaxed, unless that child is also its last.
    /// </remarks>
    internal static Node<T>? RemoveFirstLeaf<T>(Node<T> node, int shift, out LeafNode<T> leaf)
    {
        if (shift == 0)
        {
            leaf = AsLeaf(node);
            return null;
        }

        var inode = AsInternal(node);
        var len = inode.Len;
        var newFirst = RemoveFirstLeaf(inode.Children[0]!, shift - Constants.RRB_BITS, out leaf);
        var removed = leaf.Len;

        if (newFirst == null)
        {
            if (len == 1) return null;

            var rest = new Node<T>?[len - 1];
            Array.Copy(inode.Children, 1, rest, 0, len - 1);

            int[]? restTable = null;
            if (inode.SizeTable != null)
            {
                restTable = new int[len - 1];
                for (var i = 0; i < len - 1; i++) restTable[i] = inode.SizeTable[i + 1] - removed;
            }

            return new InternalNode<T>(rest, restTable, len - 1, OwnerId.None);
        }

        var children = new Node<T>?[len];
        Array.Copy(inode.Children, children, len);
        children[0] = newFirst;

        int[]? table = null;
        if (inode.IsRelaxed() || len > 1 || newFirst.IsRelaxed())
        {
            table = inode.SizeTable != null ? (int[])inode.SizeTable.Clone() : DenseTable(inode, shift);
            for (var i = 0; i < len; i++) table[i] -= removed;
        }

        return new InternalNode<T>(children, table, len, OwnerId.None);
    }

    // ---------------------------------------------------------------------
    // The Gap: a dense tree whose first slots are phantoms (see Phantom<T>).
    // Positions here are the tree's own, Gap included.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The dense tree with <paramref name="leaf" /> at <paramref name="pos" />,
    ///     a leaf's start in the Gap, where a phantom leaf was. The path to it is
    ///     copied, phantoms on it as well, and the rest is shared.
    /// </summary>
    internal static Node<T> PlaceLeaf<T>(Node<T> node, int shift, int pos, LeafNode<T> leaf)
    {
        var inode = AsInternal(node);
        var d = (pos >> shift) & Constants.RRB_MASK;
        var len = inode.Len;

        var children = new Node<T>?[len];
        Array.Copy(inode.Children, children, len);
        children[d] = shift == Constants.RRB_BITS
            ? leaf
            : PlaceLeaf(inode.Children[d]!, shift - Constants.RRB_BITS, pos, leaf);

        return new InternalNode<T>(children, null, len, OwnerId.None);
    }

    /// <summary>
    ///     The dense tree with every leaf up to and including the one that holds
    ///     <paramref name="pos" /> made a phantom. That leaf is handed back in
    ///     <paramref name="leaf" />. Every child left of the path becomes a phantom
    ///     too, so that nothing of what is dropped stays reachable. Null when the
    ///     tree is a leaf: then it is all handed back.
    ///     <paramref name="skip" /> of the node's first children, all left of the
    ///     path, are left out rather than made phantoms: at the root that is what
    ///     NormalizeGap would cut off, and the caller takes them off the Gap.
    /// </summary>
    internal static Node<T>? PhantomUpTo<T>(Node<T> node, int shift, int pos, out LeafNode<T> leaf, int skip = 0)
    {
        if (shift == 0)
        {
            leaf = AsLeaf(node);
            return null;
        }

        var inode = AsInternal(node);
        var d = (pos >> shift) & Constants.RRB_MASK;
        var len = inode.Len - skip;
        var childShift = shift - Constants.RRB_BITS;
        var phantom = Phantom<T>.At(childShift);

        var children = new Node<T>?[len];
        Array.Copy(inode.Children, skip, children, 0, len);
        for (var i = 0; i < d - skip; i++) children[i] = phantom;

        if (childShift == 0)
        {
            // Above the leaves, skip may take the leaf on the path too.
            leaf = AsLeaf(inode.Children[d]!);
            if (d >= skip) children[d - skip] = phantom;
        }
        else
        {
            children[d - skip] = PhantomUpTo(inode.Children[d]!, childShift, pos, out leaf);
        }

        return new InternalNode<T>(children, null, len, OwnerId.None);
    }

    /// <summary>
    ///     The dense tree cut to the leaves strictly between the leaf that holds
    ///     <paramref name="first" /> and the leaf that holds <paramref name="last" />,
    ///     in one pass: every leaf up to the first one becomes a phantom, as in
    ///     PhantomUpTo, and every leaf from the last one on is cut off, as a take
    ///     would. The two leaves are handed back. Requires at least one leaf between
    ///     them, so that the tree that is left holds items. <paramref name="skip" />
    ///     is as for PhantomUpTo.
    /// </summary>
    internal static Node<T> CutDense<T>(Node<T> node, int shift, int first, int last,
        out LeafNode<T> firstLeaf, out LeafNode<T> lastLeaf, int skip = 0)
    {
        var inode = AsInternal(node);
        var dl = (first >> shift) & Constants.RRB_MASK;
        var dr = (last >> shift) & Constants.RRB_MASK;
        var childShift = shift - Constants.RRB_BITS;

        // Above the leaves, both paths go through one child: descend.
        if (dl == dr && childShift > 0)
        {
            var only = CutDense(inode.Children[dl]!, childShift, first, last, out firstLeaf, out lastLeaf);
            var onlyLen = dl + 1 - skip;
            var onlyChildren = new Node<T>?[onlyLen];
            var onlyPhantom = Phantom<T>.At(childShift);
            for (var i = 0; i < dl - skip; i++) onlyChildren[i] = onlyPhantom;
            onlyChildren[dl - skip] = only;
            return new InternalNode<T>(onlyChildren, null, onlyLen, OwnerId.None);
        }

        // The paths part here. To the right of the right path nothing is kept;
        // at the leaves, not the last leaf either.
        Node<T>? right = null;
        if (childShift == 0)
        {
            lastLeaf = AsLeaf(inode.Children[dr]!);
        }
        else
        {
            right = CutBefore(inode.Children[dr]!, childShift, last, out lastLeaf);
        }

        var end = right == null ? dr : dr + 1;
        var len = end - skip;
        var children = new Node<T>?[len];
        var phantom = Phantom<T>.At(childShift);
        for (var i = 0; i < dl - skip; i++) children[i] = phantom;

        if (childShift == 0)
        {
            // Above the leaves, skip may take the first leaf too.
            firstLeaf = AsLeaf(inode.Children[dl]!);
            if (dl >= skip) children[dl - skip] = phantom;
        }
        else
        {
            children[dl - skip] = PhantomUpTo(inode.Children[dl]!, childShift, first, out firstLeaf);
        }

        var between = dr - dl - 1;
        if (between > 0) Array.Copy(inode.Children, dl + 1, children, dl + 1 - skip, between);
        if (right != null) children[dr - skip] = right;

        return new InternalNode<T>(children, null, len, OwnerId.None);
    }

    /// <summary>
    ///     The dense tree split at the leaf that holds <paramref name="pos" />, in
    ///     one pass, into the leaves before it (<paramref name="left" />, dense, null
    ///     when there are none) and the leaves after it, after phantoms for every
    ///     leaf up to it (<paramref name="right" />, dense). The leaf is handed back.
    ///     <paramref name="skip" /> is as for PhantomUpTo, for the right half.
    /// </summary>
    internal static void SplitDense<T>(Node<T> node, int shift, int pos,
        out Node<T>? left, out Node<T> right, out LeafNode<T> leaf, int skip = 0)
    {
        var inode = AsInternal(node);
        var d = (pos >> shift) & Constants.RRB_MASK;
        var len = inode.Len;
        var childShift = shift - Constants.RRB_BITS;
        var phantom = Phantom<T>.At(childShift);

        Node<T>? childLeft;
        Node<T> childRight;
        if (childShift == 0)
        {
            leaf = AsLeaf(inode.Children[d]!);
            childLeft = null;
            childRight = phantom;
        }
        else
        {
            SplitDense(inode.Children[d]!, childShift, pos, out childLeft, out childRight, out leaf);
        }

        // Left: the children before the path, and what the path keeps.
        var leftLen = childLeft == null ? d : d + 1;
        if (leftLen == 0)
        {
            left = null;
        }
        else
        {
            var leftChildren = new Node<T>?[leftLen];
            Array.Copy(inode.Children, leftChildren, d);
            if (childLeft != null) leftChildren[d] = childLeft;
            left = new InternalNode<T>(leftChildren, null, leftLen, OwnerId.None);
        }

        // Right: phantoms before the path, what the path keeps, and the rest.
        // Above the leaves, skip may take the phantom for the leaf on the path too.
        var rightLen = len - skip;
        var rightChildren = new Node<T>?[rightLen];
        for (var i = 0; i < d - skip; i++) rightChildren[i] = phantom;
        if (d >= skip) rightChildren[d - skip] = childRight;
        if (d + 1 < len) Array.Copy(inode.Children, d + 1, rightChildren, d + 1 - skip, len - d - 1);
        right = new InternalNode<T>(rightChildren, null, rightLen, OwnerId.None);
    }

    // The dense tree with every leaf from the one that holds `pos` on cut off,
    // that leaf handed back. Null when nothing is left.
    private static Node<T>? CutBefore<T>(Node<T> node, int shift, int pos, out LeafNode<T> leaf)
    {
        var inode = AsInternal(node);
        var d = (pos >> shift) & Constants.RRB_MASK;
        var childShift = shift - Constants.RRB_BITS;

        Node<T>? right = null;
        if (childShift == 0)
            leaf = AsLeaf(inode.Children[d]!);
        else
            right = CutBefore(inode.Children[d]!, childShift, pos, out leaf);

        var len = right == null ? d : d + 1;
        if (len == 0) return null;

        var children = new Node<T>?[len];
        Array.Copy(inode.Children, children, d);
        if (right != null) children[d] = right;
        return new InternalNode<T>(children, null, len, OwnerId.None);
    }

    /// <summary>
    ///     A root one level up from <paramref name="root" />, a dense tree with no
    ///     Gap, with the old root as its last child and phantoms before it: a Gap of
    ///     31 of the old root's capacity. Null when that would not fit an int index.
    /// </summary>
    internal static Node<T>? GrowForGap<T>(Node<T> root, ref int shift, out int gap)
    {
        var newShift = shift + Constants.RRB_BITS;

        // The new root holds 32 << newShift slots, which must fit an int.
        if (newShift > 25)
        {
            gap = 0;
            return null;
        }

        var children = new Node<T>?[Constants.RRB_BRANCHING];
        var phantom = Phantom<T>.At(shift);
        for (var i = 0; i < Constants.RRB_BRANCHING - 1; i++) children[i] = phantom;
        children[Constants.RRB_BRANCHING - 1] = root;

        gap = (Constants.RRB_BRANCHING - 1) << newShift;
        shift = newShift;
        return new InternalNode<T>(children, null, Constants.RRB_BRANCHING, OwnerId.None);
    }

    /// <summary>
    ///     How many items the first leaf of the tree holds.
    /// </summary>
    internal static int FirstLeafLen<T>(Node<T> node, int shift)
    {
        while (shift > 0)
        {
            node = AsInternal(node).Children[0]!;
            shift -= Constants.RRB_BITS;
        }

        return node.Len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node<T> CreatePath<T>(int shift, LeafNode<T> tail, OwnerId token)
    {
        // Base Case: Leaf
        if (shift == 0) return tail;

        // Every node on the path is dense: its only child is its last, which
        // may be part-full. A size table here would put a relaxed node under
        // a dense parent, which dense indexing does not look for. AppendChild
        // relaxes a node once a sibling follows a part-full child.
        var child = CreatePath(shift - Constants.RRB_BITS, tail, token);
        var node = new InternalNode<T>(1, token);
        node.Children[0] = child;
        return node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node<T> CreateNewParent<T>(Node<T> left, Node<T> right, OwnerId token)
    {
        if (left.Len < Constants.RRB_BRANCHING)
        {
            var sizeTable = new int[!token.IsNone ? Constants.RRB_BRANCHING : 2];
            sizeTable[0] = left.Len;
            sizeTable[1] = left.Len + right.Len;

            var children = new Node<T>?[!token.IsNone ? Constants.RRB_BRANCHING : 2];
            children[0] = left;
            children[1] = right;

            return new InternalNode<T>(children, sizeTable, 2, token);
        }

        var parent = new InternalNode<T>(2, token);
        parent.Children[0] = left;
        parent.Children[1] = right;
        return parent;
    }

    // This is a method to get the child index. If the node is dense it does a regular dense search
    // if it is relaxed, it uses AVX to search 8 elements at a time.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int childIndex, int relativeIndex) GetChildIndexAvx<T>(InternalNode<T> node, int index,
        int shift)
    {
        // Dense / Balanced Path (No SizeTable)
        if (node.IsDense())
        {
            var childIndex = (index >> shift) & Constants.RRB_MASK;
            var childStart = childIndex << shift;
            return (childIndex, index - childStart);
        }

        return GetRelaxedIndex(node, index, shift);
    }

    // The child holding `index` in a relaxed node: the first whose cumulative
    // size exceeds it. A child holds at most 1 << shift items, so it is never
    // left of index >> shift, and the search starts there.
    //
    // How far right of that it lies depends on how full the children are, and a
    // loop that stops there mispredicts its exit on nearly every lookup. The
    // table is increasing, so in a window of 8 entries that starts at or before
    // the answer, the entries <= index are exactly those before it: one compare
    // and a popcount count the steps. The loop below only runs when the answer
    // is 8 or more past the window, or the node has fewer than 8 children.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int childIndex, int relativeIndex) GetRelaxedIndex<T>(InternalNode<T> node, int index, int shift)
    {
        int len = node.Len;
        int last = len - 1;
        int[] table = node.SizeTable!;

        int i = index >> shift;
        if (Vector256.IsHardwareAccelerated && len >= 8)
        {
            int start = Math.Min(i, len - 8);
            var window = Vector256.LoadUnsafe(ref table[start]);
            uint before = Vector256.LessThanOrEqual(window, Vector256.Create(index)).ExtractMostSignificantBits();
            i = start + BitOperations.PopCount(before);
        }

        if (i > last) i = last;
        while (i < last && table[i] <= index) i++;

        var prev = i > 0 ? table[i - 1] : 0;
        return (i, index - prev);
    }
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static InternalNode<T> AsInternal<T>(Node<T> node)
    {
#if DEBUG
    if (node is InternalNode<T> internalNode)
    {
        return internalNode;
    }
    throw new InvalidCastException($"Expected InternalNode, but found {node.GetType().Name}. Check your shift/height logic.");
#else
        return Unsafe.As<InternalNode<T>>(node);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LeafNode<T> AsLeaf<T>(Node<T> node)
    {
#if DEBUG
    if (node is LeafNode<T> leafNode)
    {
        return leafNode;
    }
    throw new InvalidCastException($"Expected LeafNode, but found {node.GetType().Name}. Check your shift/height logic.");
#else
        return Unsafe.As<LeafNode<T>>(node);
#endif
    }

    // -----------------------------------------------------------------------
    // The search step invariant
    // -----------------------------------------------------------------------
    //
    // A node may have at most RRB_EXTRAS more children than the fewest that
    // could hold what they hold (AllowedChildren).
    //  - lib/std/vec.protobjo's insert and remove keep it with the helpers
    //    below, through RrbNodes.
    //  - ExecuteConcatPlan checks every node it makes from whole children, and
    //    repacks the ones that break it.

    // How much the child at `i` of a node at `shift` holds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ChildSize<T>(InternalNode<T> node, int i, int shift)
    {
        if (node.SizeTable != null)
            return node.SizeTable[i] - (i > 0 ? node.SizeTable[i - 1] : 0);
        if (i < node.Len - 1) return 1 << shift;
        return CountTree(node.Children[i]!, shift - Constants.RRB_BITS);
    }

    // The slots the first `count` children hold between them. At the level
    // above the leaves a slot is an item, so that is their sizes added up and
    // no child has to be read.
    private static int SlotsOf<T>(Node<T>?[] children, int[] sizes, int count, int shift)
    {
        var slots = 0;
        if (shift == Constants.RRB_BITS)
            for (var i = 0; i < count; i++) slots += sizes[i];
        else
            for (var i = 0; i < count; i++) slots += children[i]!.Len;
        return slots;
    }

    // A node at `shift` over the first `count` children, each holding sizes[i],
    // kept as they are when they keep the invariant and repacked by a
    // concatenation plan when they do not. Two nodes come back when more than
    // a node's worth of children are left, each with half.
    internal static InsertResult<T> BuildChecked<T>(Node<T>?[] children, int[] sizes, int count, int shift)
    {
        var slots = SlotsOf(children, sizes, count, shift);

        if (count <= AllowedChildren(slots))
        {
            if (count <= Constants.RRB_BRANCHING)
                return new InsertResult<T>(Slab(children, sizes, 0, count));

            // Only too many for one node: split as they are, in halves, so
            // both have room for the inserts that come next.
            var mid = count / 2;
            return new InsertResult<T>(Slab(children, sizes, 0, mid), Slab(children, sizes, mid, count - mid));
        }

        // Spread evenly over one node more than the fewest that would do.
        // Packing them full instead leaves no neighbour with room, so the next
        // split under this node is kept, and two or three of those break the
        // invariant again. One more than the fewest is within the invariant,
        // and fewer than `count`, which broke it.
        var topLen = (slots - 1) / Constants.RRB_BRANCHING + 2;
        Span<int> plan = stackalloc int[topLen];
        for (var i = 0; i < topLen; i++)
            plan[i] = slots / topLen + (i < slots % topLen ? 1 : 0);

        var all = new ReadOnlySpan<Node<T>>(children!, 0, count);
        return new InsertResult<T>(ExecuteConcatPlan(all, new ReadOnlySpan<int>(sizes, 0, count), plan, topLen, shift));
    }

    // A relaxed node over `count` of the children starting at `start`.
    private static InternalNode<T> Slab<T>(Node<T>?[] children, int[] sizes, int start, int count)
    {
        var kids = new Node<T>?[count];
        var table = new int[count];
        var sum = 0;
        for (var i = 0; i < count; i++)
        {
            kids[i] = children[start + i];
            sum += sizes[start + i];
            table[i] = sum;
        }

        return new InternalNode<T>(kids, table, count, OwnerId.None);
    }

    // A root at most a level taller than the `treeItems` under it need. A root
    // with one child gives way to the child, which every slice already did.
    // Past that, a tree more than a level too tall is lowered while the root's
    // grandchildren fit in one node: they become the children of a new root
    // one level down. That is the root a slice leaves with two thin spines
    // under it, and the one a remove leaves when most of the list has gone.
    // Each step copies at most a node's worth of children, and a tree within
    // a level of its height is left as it is, which is the usual case.
    internal static Node<T>? LowerRoot<T>(Node<T>? root, ref int shift, int treeItems)
    {
        var least = 0;
        for (var capacity = (long)Constants.RRB_BRANCHING; capacity < treeItems; capacity <<= Constants.RRB_BITS)
            least++;

        while (root != null && shift > 0)
        {
            var node = AsInternal(root);
            if (node.Len == 1)
            {
                root = node.Children[0];
                shift -= Constants.RRB_BITS;
                continue;
            }

            if (shift == Constants.RRB_BITS || shift / Constants.RRB_BITS <= least + 1) break;

            var total = SlotsOfChildren(node.Children, node.Len);
            if (total > Constants.RRB_BRANCHING) break;

            var childShift = shift - Constants.RRB_BITS;
            var grandchildren = new Node<T>?[total];
            var sizes = new int[total];
            var k = 0;
            for (var i = 0; i < node.Len; i++)
            {
                var child = AsInternal(node.Children[i]!);
                for (var j = 0; j < child.Len; j++)
                {
                    grandchildren[k] = child.Children[j];
                    sizes[k++] = ChildSize(child, j, childShift);
                }
            }

            root = BuildChecked(grandchildren, sizes, total, childShift).NewNode;
            shift = childShift;
        }

        return root;
    }

    // The cumulative size table of a dense node at `shift`, worked out: every
    // child but the last is full.
    internal static int[] DenseTable<T>(InternalNode<T> node, int shift)
    {
        var len = node.Len;
        var table = new int[len];
        for (var i = 0; i < len - 1; i++) table[i] = (i + 1) << shift;
        table[len - 1] = ((len - 1) << shift) + CountTree(node.Children[len - 1]!, shift - Constants.RRB_BITS);
        return table;
    }

    // The slots the first `count` children of a node above the leaf parents
    // hold between them.
    internal static int SlotsOfChildren<T>(Node<T>?[] children, int count)
    {
        var slots = 0;
        for (var i = 0; i < count; i++) slots += children[i]!.Len;
        return slots;
    }

    // Helper return struct to avoid Tuple allocation
    [NonUserCode]
    internal readonly struct InsertResult<T>(Node<T> newNode, Node<T>? overflow = null)
    {
        public readonly Node<T> NewNode = newNode;
        public readonly Node<T>? Overflow = overflow; // If not null, the node split
    }
    
}