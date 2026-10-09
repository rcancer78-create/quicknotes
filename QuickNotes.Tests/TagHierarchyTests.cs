using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

public class TagHierarchyTests
{
    [Fact]
    public void Scenario2_TagHierarchy_DescendantIdsResolution()
    {
        // IT (1) -> Database (2) -> Oracle (3)
        var it = new Tag { Id = 1, Name = "IT", ParentTagId = null };
        var db = new Tag { Id = 2, Name = "Database", ParentTagId = 1 };
        var oracle = new Tag { Id = 3, Name = "Oracle", ParentTagId = 2 };

        var allTags = new List<Tag> { it, db, oracle };

        // Querying IT should contain 1, 2, 3
        var itDescendants = TagHierarchyService.GetTagAndDescendantIds(1, allTags);
        Assert.Equal(3, itDescendants.Count);
        Assert.Contains(1, itDescendants);
        Assert.Contains(2, itDescendants);
        Assert.Contains(3, itDescendants);

        // Querying Database should contain 2, 3
        var dbDescendants = TagHierarchyService.GetTagAndDescendantIds(2, allTags);
        Assert.Equal(2, dbDescendants.Count);
        Assert.Contains(2, dbDescendants);
        Assert.Contains(3, dbDescendants);

        // Querying Oracle should contain 3
        var oracleDescendants = TagHierarchyService.GetTagAndDescendantIds(3, allTags);
        Assert.Single(oracleDescendants);
        Assert.Contains(3, oracleDescendants);
    }

    [Fact]
    public void CyclePrevention_DirectSelfParent_ReturnsTrue()
    {
        var tagA = new Tag { Id = 1, Name = "A" };
        var allTags = new List<Tag> { tagA };

        bool createsCycle = TagHierarchyService.WouldCreateCycle(1, 1, allTags);
        Assert.True(createsCycle);
    }

    [Fact]
    public void CyclePrevention_IndirectCycle_A_B_C_A_ReturnsTrue()
    {
        // A (1) -> B (2) -> C (3)
        var tagA = new Tag { Id = 1, Name = "A", ParentTagId = null };
        var tagB = new Tag { Id = 2, Name = "B", ParentTagId = 1 };
        var tagC = new Tag { Id = 3, Name = "C", ParentTagId = 2 };

        var allTags = new List<Tag> { tagA, tagB, tagC };

        // Attempting to set C (3) as parent of A (1): A -> B -> C -> A
        bool createsCycle = TagHierarchyService.WouldCreateCycle(1, 3, allTags);
        Assert.True(createsCycle);

        // Setting null as parent of A is valid
        Assert.False(TagHierarchyService.WouldCreateCycle(1, null, allTags));

        // Setting A as parent of B is valid (it's already)
        Assert.False(TagHierarchyService.WouldCreateCycle(2, 1, allTags));
    }
}
