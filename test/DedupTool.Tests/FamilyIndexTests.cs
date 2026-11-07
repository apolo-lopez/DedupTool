using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class FamilyIndexTests
{
    // --- Root resolution (task 2.1 verify criteria) ---

    [Fact]
    public void SimpleParent_ResolvesToItsParentsRoot()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var attachment = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var index = new FamilyIndex(new[] { email, attachment });

        Assert.Same(email, index.ResolveRoot(attachment));
        Assert.Same(email, index.ResolveRoot(email));
    }

    [Fact]
    public void NestedChain_EmailA_B_ResolvesBToTheEmail()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var a = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var b = TestDocuments.Attachment("B", "c1", "h3", parentId: "A");
        var index = new FamilyIndex(new[] { email, a, b });

        Assert.Same(email, index.ResolveRoot(b));
        Assert.Same(email, index.ResolveRoot(a));
        Assert.Same(email, index.ResolveRoot(email));
    }

    [Fact]
    public void LooseFile_IsItsOwnRoot()
    {
        var loose = TestDocuments.Loose("L", "c1", "h1");
        var index = new FamilyIndex(new[] { loose });

        Assert.Same(loose, index.ResolveRoot(loose));
    }

    // --- Index & children map ---

    [Fact]
    public void Index_LooksUpDocumentsById()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var index = new FamilyIndex(new[] { email });

        Assert.Same(email, index.Find("E"));
        Assert.Null(index.Find("missing"));
    }

    [Fact]
    public void ChildrenMap_GroupsChildrenByParentId()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var a = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var b = TestDocuments.Attachment("B", "c1", "h3", parentId: "E");
        var loose = TestDocuments.Loose("L", "c1", "h4");
        var index = new FamilyIndex(new[] { email, a, b, loose });

        Assert.Equal(new[] { "A", "B" }, index.ChildrenOf("E").Select(d => d.DocId));
        Assert.Empty(index.ChildrenOf("B"));   // leaf
        Assert.Empty(index.ChildrenOf("L"));   // loose files are never parents here
        Assert.Empty(index.ChildrenOf("missing"));
    }

    // --- Guards ---

    [Fact]
    public void UnknownParent_WalkStopsAtTheDocumentItself()
    {
        var orphan = TestDocuments.Attachment("A", "c1", "h1", parentId: "missing");
        var index = new FamilyIndex(new[] { orphan });

        Assert.Same(orphan, index.ResolveRoot(orphan));
    }

    [Fact]
    public void ParentCycle_DoesNotLoopForever()
    {
        var x = TestDocuments.Attachment("X", "c1", "h1", parentId: "Y");
        var y = TestDocuments.Attachment("Y", "c1", "h2", parentId: "X");
        var index = new FamilyIndex(new[] { x, y });

        // Cycle guard must terminate and hand back one of the cycle members
        // (validation in task 2.2 rejects such batches with a warning).
        Assert.Contains(index.ResolveRoot(x), new[] { x, y });
        Assert.Contains(index.ResolveRoot(y), new[] { x, y });
    }

    [Fact]
    public void ResolveRoot_IsMemoized_ReturnsTheSameInstance()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var a = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var index = new FamilyIndex(new[] { email, a });

        var first = index.ResolveRoot(a);
        var second = index.ResolveRoot(a);

        Assert.Same(first, second);
    }
}
