using System.Numerics;
using System.Text;

namespace Engine.Graphics;

/// <summary>Identifies interpolation performed between animation keys.</summary>
public enum AnimationInterpolation
{
    /// <summary>Holds the preceding key value.</summary>
    Step,

    /// <summary>Linearly interpolates vectors and spherically interpolates rotations.</summary>
    Linear
}

/// <summary>Stores one local joint translation, orientation, and scale.</summary>
/// <param name="Translation">Parent-relative translation.</param>
/// <param name="Rotation">Parent-relative normalized orientation.</param>
/// <param name="Scale">Parent-relative scale.</param>
public readonly record struct JointTransform(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale)
{
    /// <summary>Gets the identity transform.</summary>
    public static JointTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Builds the engine's row-vector local transform matrix.</summary>
    /// <returns>Scale, rotation, and translation composed in local order.</returns>
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>Describes one skeleton joint in parent-before-child order.</summary>
/// <param name="Name">Display and animation-binding name.</param>
/// <param name="ParentIndex">Parent joint index, or -1 for a root.</param>
/// <param name="BindTransform">Parent-relative bind transform.</param>
/// <param name="InverseBindMatrix">Mesh-space inverse bind matrix.</param>
public readonly record struct SkeletonJoint(
    string Name,
    int ParentIndex,
    JointTransform BindTransform,
    Matrix4x4 InverseBindMatrix);

/// <summary>Contains an immutable, topologically ordered skeleton.</summary>
public sealed class SkeletonResource
{
    private readonly SkeletonJoint[] _joints;

    /// <summary>Gets skeleton joints in parent-before-child order.</summary>
    public IReadOnlyList<SkeletonJoint> Joints => _joints;

    /// <summary>Gets the number of joints.</summary>
    public int JointCount => _joints.Length;

    /// <summary>Creates a validated skeleton.</summary>
    /// <param name="joints">Topologically ordered joints.</param>
    public SkeletonResource(SkeletonJoint[] joints)
    {
        ArgumentNullException.ThrowIfNull(joints);
        _joints = joints.ToArray();
        for (var index = 0; index < _joints.Length; index++)
        {
            var joint = _joints[index];
            if (string.IsNullOrWhiteSpace(joint.Name))
                throw new ArgumentException("Skeleton joint names cannot be empty.", nameof(joints));
            if (joint.ParentIndex < -1 || joint.ParentIndex >= index)
                throw new ArgumentException(
                    "Skeleton joints must appear after their parent.", nameof(joints));
            if (!IsFinite(joint.BindTransform) || !IsFinite(joint.InverseBindMatrix))
                throw new ArgumentException("Skeleton transforms must be finite.", nameof(joints));
        }
    }

    /// <summary>Finds a joint by its exact imported name.</summary>
    /// <param name="name">Joint name.</param>
    /// <returns>The joint index, or -1 when absent.</returns>
    public int FindJoint(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _joints.Length; index++)
        {
            if (string.Equals(_joints[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    /// <summary>Checks one transform for finite numeric values.</summary>
    /// <param name="transform">Transform to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(JointTransform transform) =>
        IsFinite(transform.Translation) && IsFinite(transform.Rotation) && IsFinite(transform.Scale);

    /// <summary>Checks a vector for finite numeric values.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Checks a quaternion for finite numeric values.</summary>
    /// <param name="value">Quaternion to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    /// <summary>Checks a matrix for finite numeric values.</summary>
    /// <param name="value">Matrix to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

/// <summary>Stores one keyed three-component animation curve.</summary>
public sealed class Vector3AnimationTrack
{
    /// <summary>Gets ascending key times in seconds.</summary>
    public float[] Times { get; }

    /// <summary>Gets values corresponding to <see cref="Times"/>.</summary>
    public Vector3[] Values { get; }

    /// <summary>Gets interpolation between keys.</summary>
    public AnimationInterpolation Interpolation { get; }

    /// <summary>Creates a validated vector track.</summary>
    /// <param name="times">Ascending key times.</param>
    /// <param name="values">Key values.</param>
    /// <param name="interpolation">Interpolation mode.</param>
    public Vector3AnimationTrack(
        float[] times,
        Vector3[] values,
        AnimationInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateKeys(times, values.Length);
        Times = times.ToArray();
        Values = values.ToArray();
        Interpolation = interpolation;
    }

    /// <summary>Samples this curve at one clip-local time.</summary>
    /// <param name="time">Clip-local time in seconds.</param>
    /// <returns>The sampled value.</returns>
    public Vector3 Sample(float time)
    {
        var first = FindKeyInterval(Times, time, out var amount);
        if (first == Times.Length - 1 || Interpolation == AnimationInterpolation.Step)
            return Values[first];
        return Vector3.Lerp(Values[first], Values[first + 1], amount);
    }

    /// <summary>Validates common animation key invariants.</summary>
    /// <param name="times">Key times.</param>
    /// <param name="valueCount">Number of corresponding values.</param>
    internal static void ValidateKeys(float[] times, int valueCount)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.Length == 0 || times.Length != valueCount)
            throw new ArgumentException("Animation key times and values must have equal nonzero lengths.");
        var previous = -1f;
        for (var index = 0; index < times.Length; index++)
        {
            if (!float.IsFinite(times[index]) || times[index] < 0f || times[index] <= previous)
                throw new ArgumentException("Animation key times must be finite and strictly ascending.");
            previous = times[index];
        }
    }

    /// <summary>Finds the lower key and normalized interpolation amount.</summary>
    /// <param name="times">Ascending key times.</param>
    /// <param name="time">Sample time.</param>
    /// <param name="amount">Normalized amount to the following key.</param>
    /// <returns>The lower key index.</returns>
    internal static int FindKeyInterval(float[] times, float time, out float amount)
    {
        if (time <= times[0])
        {
            amount = 0f;
            return 0;
        }
        var last = times.Length - 1;
        if (time >= times[last])
        {
            amount = 0f;
            return last;
        }
        var low = 0;
        var high = last;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (times[middle] <= time)
                low = middle;
            else
                high = middle;
        }
        amount = (time - times[low]) / (times[high] - times[low]);
        return low;
    }
}

/// <summary>Stores one keyed quaternion animation curve.</summary>
public sealed class QuaternionAnimationTrack
{
    /// <summary>Gets ascending key times in seconds.</summary>
    public float[] Times { get; }

    /// <summary>Gets normalized rotations corresponding to <see cref="Times"/>.</summary>
    public Quaternion[] Values { get; }

    /// <summary>Gets interpolation between keys.</summary>
    public AnimationInterpolation Interpolation { get; }

    /// <summary>Creates a validated rotation track.</summary>
    /// <param name="times">Ascending key times.</param>
    /// <param name="values">Key rotations.</param>
    /// <param name="interpolation">Interpolation mode.</param>
    public QuaternionAnimationTrack(
        float[] times,
        Quaternion[] values,
        AnimationInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(values);
        Vector3AnimationTrack.ValidateKeys(times, values.Length);
        Times = times.ToArray();
        Values = new Quaternion[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].LengthSquared() <= float.Epsilon)
                throw new ArgumentException("Animation rotations cannot be zero.", nameof(values));
            Values[index] = Quaternion.Normalize(values[index]);
        }
        Interpolation = interpolation;
    }

    /// <summary>Samples this curve at one clip-local time.</summary>
    /// <param name="time">Clip-local time in seconds.</param>
    /// <returns>The sampled normalized rotation.</returns>
    public Quaternion Sample(float time)
    {
        var first = Vector3AnimationTrack.FindKeyInterval(Times, time, out var amount);
        if (first == Times.Length - 1 || Interpolation == AnimationInterpolation.Step)
            return Values[first];
        return Quaternion.Normalize(Quaternion.Slerp(Values[first], Values[first + 1], amount));
    }
}

/// <summary>Groups optional transform curves targeting one skeleton joint.</summary>
/// <param name="Translation">Optional translation curve.</param>
/// <param name="Rotation">Optional rotation curve.</param>
/// <param name="Scale">Optional scale curve.</param>
public sealed record JointAnimationTrack(
    Vector3AnimationTrack? Translation,
    QuaternionAnimationTrack? Rotation,
    Vector3AnimationTrack? Scale);

/// <summary>Contains one animation clip with tracks aligned to a skeleton.</summary>
public sealed class AnimationClipResource
{
    private readonly JointAnimationTrack?[] _tracks;

    /// <summary>Gets the imported display name.</summary>
    public string Name { get; }

    /// <summary>Gets clip duration in seconds.</summary>
    public float Duration { get; }

    /// <summary>Gets tracks by skeleton joint index.</summary>
    public IReadOnlyList<JointAnimationTrack?> Tracks => _tracks;

    /// <summary>Creates a validated animation clip.</summary>
    /// <param name="name">Imported clip name.</param>
    /// <param name="duration">Duration in seconds.</param>
    /// <param name="tracks">Tracks aligned to skeleton joints.</param>
    public AnimationClipResource(string name, float duration, JointAnimationTrack?[] tracks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!float.IsFinite(duration) || duration < 0f)
            throw new ArgumentOutOfRangeException(nameof(duration));
        Name = name;
        Duration = duration;
        _tracks = tracks.ToArray();
    }
}

/// <summary>Stores four joint indices and their normalized vertex weights.</summary>
public struct SkinInfluence
{
    /// <summary>Gets or sets the first joint index.</summary>
    public uint Joint0;
    /// <summary>Gets or sets the second joint index.</summary>
    public uint Joint1;
    /// <summary>Gets or sets the third joint index.</summary>
    public uint Joint2;
    /// <summary>Gets or sets the fourth joint index.</summary>
    public uint Joint3;
    /// <summary>Gets or sets weights corresponding to the four joint indices.</summary>
    public Vector4 Weights;

    /// <summary>Creates one four-joint influence.</summary>
    /// <param name="joint0">First joint index.</param>
    /// <param name="joint1">Second joint index.</param>
    /// <param name="joint2">Third joint index.</param>
    /// <param name="joint3">Fourth joint index.</param>
    /// <param name="weights">Four influence weights.</param>
    public SkinInfluence(uint joint0, uint joint1, uint joint2, uint joint3, Vector4 weights)
    {
        Joint0 = joint0;
        Joint1 = joint1;
        Joint2 = joint2;
        Joint3 = joint3;
        var sum = weights.X + weights.Y + weights.Z + weights.W;
        Weights = sum > float.Epsilon ? weights / sum : new Vector4(1f, 0f, 0f, 0f);
    }
}

/// <summary>Groups renderer handles created for one skinned mesh instance.</summary>
/// <param name="Mesh">Immutable skinned geometry handle.</param>
/// <param name="Palette">Mutable joint-palette handle.</param>
public readonly record struct SkinnedMeshHandles(
    MeshHandle Mesh,
    SkinPaletteHandle Palette);

/// <summary>Contains indexed geometry, skin weights, skeleton, and imported clips.</summary>
public sealed class SkinnedMeshResource
{
    private const string Magic = "NSKIN001";
    private readonly AnimationClipResource[] _animations;

    /// <summary>Gets base indexed geometry and material ranges.</summary>
    public StaticMeshResource Mesh { get; }

    /// <summary>Gets one skin influence per mesh vertex.</summary>
    public SkinInfluence[] Influences { get; }

    /// <summary>Gets the skeleton referenced by vertex influences.</summary>
    public SkeletonResource Skeleton { get; }

    /// <summary>Gets imported animation clips.</summary>
    public IReadOnlyList<AnimationClipResource> Animations => _animations;

    /// <summary>Gets the source mesh-node transform applied after skin deformation.</summary>
    public Matrix4x4 MeshNodeTransform { get; }

    /// <summary>Creates validated skinned geometry and animation data.</summary>
    /// <param name="mesh">Base indexed mesh.</param>
    /// <param name="influences">One influence per vertex.</param>
    /// <param name="skeleton">Bound skeleton.</param>
    /// <param name="animations">Clips aligned to the skeleton.</param>
    public SkinnedMeshResource(
        StaticMeshResource mesh,
        SkinInfluence[] influences,
        SkeletonResource skeleton,
        AnimationClipResource[] animations)
        : this(mesh, influences, skeleton, animations, Matrix4x4.Identity)
    {
    }

    /// <summary>Creates validated skinned geometry with its source mesh-node transform.</summary>
    /// <param name="mesh">Base indexed mesh.</param>
    /// <param name="influences">One influence per vertex.</param>
    /// <param name="skeleton">Bound skeleton.</param>
    /// <param name="animations">Clips aligned to the skeleton.</param>
    /// <param name="meshNodeTransform">Source mesh-node world transform.</param>
    public SkinnedMeshResource(
        StaticMeshResource mesh,
        SkinInfluence[] influences,
        SkeletonResource skeleton,
        AnimationClipResource[] animations,
        Matrix4x4 meshNodeTransform)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(influences);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animations);
        if (!IsFinite(meshNodeTransform))
            throw new ArgumentOutOfRangeException(nameof(meshNodeTransform));
        if (influences.Length != mesh.Vertices.Length)
            throw new ArgumentException("Every skinned vertex requires one influence.", nameof(influences));
        for (var index = 0; index < influences.Length; index++)
        {
            var influence = influences[index];
            if (influence.Joint0 >= skeleton.JointCount ||
                influence.Joint1 >= skeleton.JointCount ||
                influence.Joint2 >= skeleton.JointCount ||
                influence.Joint3 >= skeleton.JointCount)
            {
                throw new ArgumentException("A skin influence references a missing joint.",
                    nameof(influences));
            }
        }
        for (var index = 0; index < animations.Length; index++)
        {
            if (animations[index].Tracks.Count != skeleton.JointCount)
                throw new ArgumentException("Animation tracks must align to skeleton joints.",
                    nameof(animations));
        }
        Mesh = mesh;
        Influences = influences.ToArray();
        Skeleton = skeleton;
        _animations = animations.ToArray();
        MeshNodeTransform = meshNodeTransform;
    }

    /// <summary>Finds an imported animation by exact name.</summary>
    /// <param name="name">Clip name.</param>
    /// <returns>The matching clip, or null.</returns>
    public AnimationClipResource? FindAnimation(string? name)
    {
        if (name is null)
            return _animations.Length > 0 ? _animations[0] : null;
        for (var index = 0; index < _animations.Length; index++)
        {
            if (string.Equals(_animations[index].Name, name, StringComparison.Ordinal))
                return _animations[index];
        }
        return null;
    }

    /// <summary>Composes the source mesh-node transform with one scene-instance transform.</summary>
    /// <param name="instanceTransform">Scene-instance model transform.</param>
    /// <returns>Model transform applied after skin deformation.</returns>
    public Matrix4x4 ComposeModelTransform(Matrix4x4 instanceTransform) =>
        MeshNodeTransform * instanceTransform;

    /// <summary>Writes one versioned Nico skinned-mesh artifact.</summary>
    /// <param name="stream">Writable artifact stream.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("NSKIN001"u8);
        writer.Write(2u);
        WriteMesh(writer);
        WriteSkeleton(writer);
        writer.Write(checked((uint)_animations.Length));
        for (var index = 0; index < _animations.Length; index++)
            WriteAnimation(writer, _animations[index]);
    }

    /// <summary>Reads one versioned Nico skinned-mesh artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded resource.</returns>
    public static SkinnedMeshResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Skinned mesh artifact has an invalid signature.");
        var version = reader.ReadUInt32();
        if (version is not 1u and not 2u)
            throw new InvalidDataException("Skinned mesh artifact version is unsupported.");
        var vertexCount = checked((int)reader.ReadUInt32());
        var indexCount = checked((int)reader.ReadUInt32());
        var materialSlot = reader.ReadInt32();
        var meshNodeTransform = version >= 2u ? ReadMatrix(reader) : Matrix4x4.Identity;
        var vertices = new ModelVertex[vertexCount];
        var influences = new SkinInfluence[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            vertices[index] = new ModelVertex(ReadVector3(reader), ReadVector3(reader),
                ReadVector2(reader), ReadVector4(reader));
            influences[index] = new SkinInfluence(reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt32(), ReadVector4(reader));
        }
        var indices = new uint[indexCount];
        for (var index = 0; index < indexCount; index++)
            indices[index] = reader.ReadUInt32();
        var mesh = new StaticMeshResource(vertices, indices,
            [new Submesh(0, checked((uint)indices.Length), materialSlot)]);
        var jointCount = checked((int)reader.ReadUInt32());
        var joints = new SkeletonJoint[jointCount];
        for (var index = 0; index < joints.Length; index++)
        {
            joints[index] = new SkeletonJoint(reader.ReadString(), reader.ReadInt32(),
                new JointTransform(ReadVector3(reader), ReadQuaternion(reader), ReadVector3(reader)),
                ReadMatrix(reader));
        }
        var skeleton = new SkeletonResource(joints);
        var animationCount = checked((int)reader.ReadUInt32());
        var animations = new AnimationClipResource[animationCount];
        for (var index = 0; index < animations.Length; index++)
            animations[index] = ReadAnimation(reader, jointCount);
        if (stream.CanSeek && stream.Position != stream.Length)
            throw new InvalidDataException("Skinned mesh artifact contains trailing data.");
        return new SkinnedMeshResource(mesh, influences, skeleton, animations,
            meshNodeTransform);
    }

    /// <summary>Writes base mesh and influence payloads.</summary>
    /// <param name="writer">Artifact writer.</param>
    private void WriteMesh(BinaryWriter writer)
    {
        writer.Write(checked((uint)Mesh.Vertices.Length));
        writer.Write(checked((uint)Mesh.Indices.Length));
        writer.Write(Mesh.Submeshes.Count > 0 ? Mesh.Submeshes[0].MaterialSlot : -1);
        Write(writer, MeshNodeTransform);
        for (var index = 0; index < Mesh.Vertices.Length; index++)
        {
            var vertex = Mesh.Vertices[index];
            Write(writer, vertex.Position);
            Write(writer, vertex.Normal);
            Write(writer, vertex.TexCoord);
            Write(writer, vertex.Tangent);
            var influence = Influences[index];
            writer.Write(influence.Joint0);
            writer.Write(influence.Joint1);
            writer.Write(influence.Joint2);
            writer.Write(influence.Joint3);
            Write(writer, influence.Weights);
        }
        for (var index = 0; index < Mesh.Indices.Length; index++)
            writer.Write(Mesh.Indices[index]);
    }

    /// <summary>Writes skeleton joints.</summary>
    /// <param name="writer">Artifact writer.</param>
    private void WriteSkeleton(BinaryWriter writer)
    {
        writer.Write(checked((uint)Skeleton.JointCount));
        for (var index = 0; index < Skeleton.JointCount; index++)
        {
            var joint = Skeleton.Joints[index];
            writer.Write(joint.Name);
            writer.Write(joint.ParentIndex);
            Write(writer, joint.BindTransform.Translation);
            Write(writer, joint.BindTransform.Rotation);
            Write(writer, joint.BindTransform.Scale);
            Write(writer, joint.InverseBindMatrix);
        }
    }

    /// <summary>Writes one animation clip.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="animation">Clip to write.</param>
    private static void WriteAnimation(BinaryWriter writer, AnimationClipResource animation)
    {
        writer.Write(animation.Name);
        writer.Write(animation.Duration);
        for (var index = 0; index < animation.Tracks.Count; index++)
        {
            var track = animation.Tracks[index];
            writer.Write(track is not null);
            if (track is null)
                continue;
            WriteTrack(writer, track.Translation);
            WriteTrack(writer, track.Rotation);
            WriteTrack(writer, track.Scale);
        }
    }

    /// <summary>Writes an optional vector curve.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="track">Optional curve.</param>
    private static void WriteTrack(BinaryWriter writer, Vector3AnimationTrack? track)
    {
        writer.Write(track is not null);
        if (track is null)
            return;
        writer.Write((byte)track.Interpolation);
        writer.Write(checked((uint)track.Times.Length));
        for (var index = 0; index < track.Times.Length; index++)
        {
            writer.Write(track.Times[index]);
            Write(writer, track.Values[index]);
        }
    }

    /// <summary>Writes an optional rotation curve.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="track">Optional curve.</param>
    private static void WriteTrack(BinaryWriter writer, QuaternionAnimationTrack? track)
    {
        writer.Write(track is not null);
        if (track is null)
            return;
        writer.Write((byte)track.Interpolation);
        writer.Write(checked((uint)track.Times.Length));
        for (var index = 0; index < track.Times.Length; index++)
        {
            writer.Write(track.Times[index]);
            Write(writer, track.Values[index]);
        }
    }

    /// <summary>Reads one animation clip.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <param name="jointCount">Expected track count.</param>
    /// <returns>The decoded clip.</returns>
    private static AnimationClipResource ReadAnimation(BinaryReader reader, int jointCount)
    {
        var name = reader.ReadString();
        var duration = reader.ReadSingle();
        var tracks = new JointAnimationTrack?[jointCount];
        for (var index = 0; index < tracks.Length; index++)
        {
            if (!reader.ReadBoolean())
                continue;
            tracks[index] = new JointAnimationTrack(
                ReadVectorTrack(reader), ReadQuaternionTrack(reader), ReadVectorTrack(reader));
        }
        return new AnimationClipResource(name, duration, tracks);
    }

    /// <summary>Reads an optional vector curve.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded curve, or null.</returns>
    private static Vector3AnimationTrack? ReadVectorTrack(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        var interpolation = ReadInterpolation(reader);
        var count = checked((int)reader.ReadUInt32());
        var times = new float[count];
        var values = new Vector3[count];
        for (var index = 0; index < count; index++)
        {
            times[index] = reader.ReadSingle();
            values[index] = ReadVector3(reader);
        }
        return new Vector3AnimationTrack(times, values, interpolation);
    }

    /// <summary>Reads an optional rotation curve.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded curve, or null.</returns>
    private static QuaternionAnimationTrack? ReadQuaternionTrack(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        var interpolation = ReadInterpolation(reader);
        var count = checked((int)reader.ReadUInt32());
        var times = new float[count];
        var values = new Quaternion[count];
        for (var index = 0; index < count; index++)
        {
            times[index] = reader.ReadSingle();
            values[index] = ReadQuaternion(reader);
        }
        return new QuaternionAnimationTrack(times, values, interpolation);
    }

    /// <summary>Reads and validates one interpolation tag.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded interpolation mode.</returns>
    private static AnimationInterpolation ReadInterpolation(BinaryReader reader)
    {
        var value = (AnimationInterpolation)reader.ReadByte();
        if (value is not AnimationInterpolation.Step and not AnimationInterpolation.Linear)
            throw new InvalidDataException("Animation interpolation is unsupported.");
        return value;
    }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X); writer.Write(value.Y);
    }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
    }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W);
    }

    /// <summary>Writes a quaternion.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Quaternion value.</param>
    private static void Write(BinaryWriter writer, Quaternion value) =>
        Write(writer, new Vector4(value.X, value.Y, value.Z, value.W));

    /// <summary>Writes a row-major matrix.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Matrix value.</param>
    private static void Write(BinaryWriter writer, Matrix4x4 value)
    {
        writer.Write(value.M11); writer.Write(value.M12); writer.Write(value.M13); writer.Write(value.M14);
        writer.Write(value.M21); writer.Write(value.M22); writer.Write(value.M23); writer.Write(value.M24);
        writer.Write(value.M31); writer.Write(value.M32); writer.Write(value.M33); writer.Write(value.M34);
        writer.Write(value.M41); writer.Write(value.M42); writer.Write(value.M43); writer.Write(value.M44);
    }

    /// <summary>Returns whether every matrix component is finite.</summary>
    /// <param name="value">Matrix to validate.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);

    /// <summary>Reads a vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>Decoded value.</returns>
    private static Vector2 ReadVector2(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Reads a vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>Decoded value.</returns>
    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Reads a vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>Decoded value.</returns>
    private static Vector4 ReadVector4(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Reads a quaternion.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>Decoded value.</returns>
    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        var value = ReadVector4(reader);
        return new Quaternion(value.X, value.Y, value.Z, value.W);
    }

    /// <summary>Reads a row-major matrix.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>Decoded value.</returns>
    private static Matrix4x4 ReadMatrix(BinaryReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

/// <summary>Evaluates one skeleton pose and reusable GPU skinning palette.</summary>
public sealed class SkeletonPose
{
    private readonly JointTransform[] _localTransforms;
    private readonly Matrix4x4[] _worldTransforms;
    private readonly Matrix4x4[] _skinMatrices;

    /// <summary>Gets the evaluated local transforms.</summary>
    public ReadOnlySpan<JointTransform> LocalTransforms => _localTransforms;

    /// <summary>Gets the evaluated mesh-space joint transforms.</summary>
    public ReadOnlySpan<Matrix4x4> WorldTransforms => _worldTransforms;

    /// <summary>Gets matrices consumed by GPU linear-blend skinning.</summary>
    public ReadOnlySpan<Matrix4x4> SkinMatrices => _skinMatrices;

    /// <summary>Creates a reusable pose initialized to bind pose.</summary>
    /// <param name="skeleton">Skeleton defining storage size and bind transforms.</param>
    public SkeletonPose(SkeletonResource skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        _localTransforms = new JointTransform[skeleton.JointCount];
        _worldTransforms = new Matrix4x4[skeleton.JointCount];
        _skinMatrices = new Matrix4x4[skeleton.JointCount];
        Evaluate(skeleton, null, 0f);
    }

    /// <summary>Evaluates a clip or bind pose without allocating.</summary>
    /// <param name="skeleton">Skeleton matching this pose.</param>
    /// <param name="clip">Optional animation clip.</param>
    /// <param name="time">Clip-local sample time.</param>
    public void Evaluate(SkeletonResource skeleton, AnimationClipResource? clip, float time)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (skeleton.JointCount != _localTransforms.Length)
            throw new ArgumentException("Skeleton does not match this pose.", nameof(skeleton));
        if (clip is not null && clip.Tracks.Count != skeleton.JointCount)
            throw new ArgumentException("Animation does not match this skeleton.", nameof(clip));
        for (var index = 0; index < skeleton.JointCount; index++)
        {
            var joint = skeleton.Joints[index];
            var transform = joint.BindTransform;
            var track = clip?.Tracks[index];
            if (track is not null)
            {
                transform = new JointTransform(
                    track.Translation?.Sample(time) ?? transform.Translation,
                    track.Rotation?.Sample(time) ?? transform.Rotation,
                    track.Scale?.Sample(time) ?? transform.Scale);
            }
            _localTransforms[index] = transform;
            var local = transform.ToMatrix();
            _worldTransforms[index] = joint.ParentIndex >= 0
                ? local * _worldTransforms[joint.ParentIndex] : local;
            _skinMatrices[index] = joint.InverseBindMatrix * _worldTransforms[index];
        }
    }
}

/// <summary>Owns playback time and an allocation-free evaluated skeleton pose.</summary>
public sealed class AnimationPlayer
{
    private float _speed = 1f;

    /// <summary>Gets the skinned resource being animated.</summary>
    public SkinnedMeshResource Resource { get; }

    /// <summary>Gets the reusable evaluated pose.</summary>
    public SkeletonPose Pose { get; }

    /// <summary>Gets or sets the current clip.</summary>
    public AnimationClipResource? Clip { get; private set; }

    /// <summary>Gets current clip-local time in seconds.</summary>
    public float Time { get; private set; }

    /// <summary>Gets or sets the signed playback-rate multiplier.</summary>
    public float Speed
    {
        get => _speed;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _speed = value;
        }
    }

    /// <summary>Gets or sets whether playback wraps at the clip boundary.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Gets or sets whether time advances.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>Creates a stopped player in bind pose.</summary>
    /// <param name="resource">Skinned resource to animate.</param>
    public AnimationPlayer(SkinnedMeshResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Resource = resource;
        Pose = new SkeletonPose(resource.Skeleton);
    }

    /// <summary>Selects a clip and resets playback time.</summary>
    /// <param name="name">Exact clip name, or null for the first clip.</param>
    /// <param name="play">Whether playback starts immediately.</param>
    /// <returns>True when a matching clip exists.</returns>
    public bool Play(string? name = null, bool play = true)
    {
        var clip = Resource.FindAnimation(name);
        if (clip is null)
        {
            Clip = null;
            Time = 0f;
            IsPlaying = false;
            Pose.Evaluate(Resource.Skeleton, null, 0f);
            return false;
        }
        Clip = clip;
        Time = Speed < 0f ? clip.Duration : 0f;
        IsPlaying = play;
        Pose.Evaluate(Resource.Skeleton, clip, Time);
        return true;
    }

    /// <summary>Advances playback and evaluates the resulting pose without allocating.</summary>
    /// <param name="deltaTime">Elapsed runtime seconds.</param>
    public void Update(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0d)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (Clip is null || !IsPlaying || deltaTime == 0d)
            return;
        var duration = Clip.Duration;
        if (duration <= 0f)
        {
            Time = 0f;
            IsPlaying = false;
        }
        else if (Loop)
        {
            Time = (float)((Time + deltaTime * Speed) % duration);
            if (Time < 0f)
                Time += duration;
        }
        else
        {
            Time = Math.Clamp((float)(Time + deltaTime * Speed), 0f, duration);
            if (Time <= 0f || Time >= duration)
                IsPlaying = false;
        }
        Pose.Evaluate(Resource.Skeleton, Clip, Time);
    }
}
