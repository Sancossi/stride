// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Transactions;
using Xunit;

namespace Stride.Core.Design.Tests.Transactions;

public class TestTransactionAbort
{
    [Fact]
    public void AbortRestoresValueWithoutReplacingPriorUndoRedo()
    {
        var stack = TransactionStackFactory.Create(8);
        var earlier = new SimpleOperation();
        using (stack.CreateTransaction()) stack.PushOperation(earlier);
        var redo = new SimpleOperation();
        using (stack.CreateTransaction()) stack.PushOperation(redo);
        stack.Rollback();
        var ids = stack.RetrieveAllTransactions().Select(x => x.Id).ToArray();
        var failed = new SimpleOperation();
        using (var transaction = stack.CreateTransaction())
        {
            stack.PushOperation(failed);
            stack.AbortTransaction(transaction);
            Assert.True(transaction.IsAborted);
        }
        Assert.False(failed.IsDone);
        Assert.True(failed.IsFrozen);
        Assert.Equal(ids, stack.RetrieveAllTransactions().Select(x => x.Id));
        Assert.True(stack.CanRollback);
        Assert.True(stack.CanRollforward);
        stack.Rollforward();
        Assert.True(redo.IsDone);
        stack.Rollback();
        stack.Rollback();
        Assert.False(earlier.IsDone);
    }

    [Fact]
    public void ChildAbortKeepsParentAndRejectsNonCurrentSharedOrKeepAlive()
    {
        var stack = TransactionStackFactory.Create(8);
        var parent = stack.CreateTransaction();
        var retained = new SimpleOperation();
        stack.PushOperation(retained);
        var child = stack.CreateTransaction();
        var discarded = new SimpleOperation();
        stack.PushOperation(discarded);
        Assert.Throws<TransactionException>(() => stack.AbortTransaction(parent));
        child.AddReference();
        Assert.Throws<TransactionException>(() => stack.AbortTransaction(child));
        child.Complete();
        stack.AbortTransaction(child);
        Assert.True(stack.TransactionInProgress);
        Assert.True(retained.IsDone);
        Assert.False(discarded.IsDone);
        var keepAlive = stack.CreateTransaction(TransactionFlags.KeepParentsAlive);
        Assert.Throws<TransactionException>(() => stack.AbortTransaction(keepAlive));
        keepAlive.Complete();
        parent.Complete();
        Assert.Single(stack.RetrieveAllTransactions());
        stack.Rollback();
        Assert.False(retained.IsDone);
        Assert.Equal(1, discarded.RollbackCount);
    }

    [Fact]
    public void ThrowingNotificationOccursAfterAbortAndCannotReenterParent()
    {
        var stack = TransactionStackFactory.Create(8);
        var parent = stack.CreateTransaction();
        var child = stack.CreateTransaction();
        var operation = new SimpleOperation();
        stack.PushOperation(operation);
        EventHandler<TransactionEventArgs> handler = (_, _) =>
        {
            Assert.True(child.IsAborted);
            Assert.Throws<TransactionException>(() => parent.Complete());
            Assert.Throws<TransactionException>(() => stack.CreateTransaction());
            throw new InvalidOperationException("subscriber failure");
        };
        stack.TransactionAborted += handler;
        Assert.Throws<InvalidOperationException>(() => stack.AbortTransaction(child));
        stack.TransactionAborted -= handler;
        child.Dispose();
        Assert.True(operation.IsFrozen);
        Assert.False(operation.IsDone);
        Assert.True(stack.TransactionInProgress);
        stack.PushOperation(new SimpleOperation());
        parent.Complete();
        Assert.Single(stack.RetrieveAllTransactions());
    }

    [Fact]
    public void FailedRollbackRemainsPoisonedAndCannotCommitOrReceiveNewEdits()
    {
        var stack = TransactionStackFactory.Create(8);
        var transaction = stack.CreateTransaction();
        stack.PushOperation(new ThrowingRollback());
        Assert.Throws<InvalidOperationException>(() => stack.AbortTransaction(transaction));
        Assert.True(transaction.HasFailedAbort);
        Assert.False(transaction.IsAborted);
        Assert.True(stack.TransactionInProgress);
        Assert.Throws<TransactionException>(() => transaction.Complete());
        Assert.Throws<TransactionException>(() => stack.CreateTransaction());
        Assert.Throws<TransactionException>(() => stack.PushOperation(new SimpleOperation()));
        Assert.Empty(stack.RetrieveAllTransactions());
    }

    private sealed class ThrowingRollback : Operation
    {
        protected override void Rollback() => throw new InvalidOperationException("rollback failed");
        protected override void Rollforward() => throw new NotSupportedException();
    }
}
