using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Graphics.Tests;

public class NodeTests
{
    /// <summary>Verifies that scene graph cycles are rejected.</summary>
    [Fact]
    public void AddChild_RejectsCycle()
    {
        var root = new Node();
        var child = new Node();
        root.AddChild(child);

        Assert.Throws<InvalidOperationException>(() => child.AddChild(root));
    }

    /// <summary>Verifies that adding the same child twice is idempotent.</summary>
    [Fact]
    public void AddChild_SameParent_DoesNotDuplicateChild()
    {
        var root = new Node();
        var child = new Node();

        root.AddChild(child);
        root.AddChild(child);

        Assert.Single(root.Children);
    }

    /// <summary>Verifies that 3D child transforms include their parent transform.</summary>
    [Fact]
    public void GetModelMatrix_IncludesParentTransform()
    {
        var parent = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var child = new Node3D { Position = new Vector3(2f, 0f, 0f) };
        parent.AddChild(child);

        var worldOrigin = Vector3.Transform(Vector3.Zero, child.GetModelMatrix());

        Assert.Equal(new Vector3(12f, 0f, 0f), worldOrigin);
    }

    /// <summary>Verifies world-space editing preserves a child's parent-relative transform.</summary>
    [Fact]
    public void SetWorldTransform_ParentedChild_ConvertsToLocalPosition()
    {
        var parent = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var child = new Node3D();
        parent.AddChild(child);

        child.SetWorldTransform(new Vector3(14f, 2f, 0f), Vector3.Zero);

        Assert.Equal(new Vector3(4f, 2f, 0f), child.Position);
        Assert.Equal(new Vector3(14f, 2f, 0f), child.GetWorldPosition());
    }

    /// <summary>Verifies Euler compatibility writes the authoritative quaternion orientation.</summary>
    [Fact]
    public void Rotation_UpdatesQuaternionOrientation()
    {
        var node = new Node3D { Rotation = new Vector3(0.3f, -0.2f, 0.4f) };
        var expected = Matrix4x4.CreateRotationZ(0.4f)
            * Matrix4x4.CreateRotationY(-0.2f)
            * Matrix4x4.CreateRotationX(0.3f);

        AssertMatrixClose(expected, Matrix4x4.CreateFromQuaternion(node.Orientation));
    }

    /// <summary>Verifies quaternion composition retains all axes through an Euler singularity.</summary>
    [Fact]
    public void Orientation_ComposesThroughEulerSingularity()
    {
        var node = new Node3D();
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        var composed = Quaternion.Normalize(Quaternion.Concatenate(pitch, roll));

        node.Orientation = composed;

        AssertMatrixClose(Matrix4x4.CreateFromQuaternion(composed), node.GetModelMatrix());
        Assert.InRange(MathF.Abs(node.Orientation.Length() - 1f), 0f, 0.0001f);
    }

    /// <summary>Compares all matrix elements within a numeric tolerance.</summary>
    /// <param name="expected">Expected matrix.</param>
    /// <param name="actual">Actual matrix.</param>
    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = new[] { expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44 };
        var actualValues = new[] { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24, actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };
        for (var index = 0; index < expectedValues.Length; index++)
            Assert.InRange(MathF.Abs(expectedValues[index] - actualValues[index]), 0f, 0.0001f);
    }
}
