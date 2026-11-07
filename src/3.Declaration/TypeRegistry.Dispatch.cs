using System.Text;
using Builder.Verification.Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Stored values of a protocol: a place a program declares with a protocol type (<c>var x: Enemy</c>,
/// <c>List[Enemy]</c>, a member variable, <c>Enemy?</c>) holds <c>Dispatched[P]</c>, a type hash and an address
/// (<see cref="RuntimeContract.Dispatch"/>). The registry knows which protocol each such type stands for, gives it the
/// protocol's routines (their bodies are written once the program's stored types are known), keeps those types per
/// protocol, and computes the type hashes.
/// </summary>
public sealed partial class TypeRegistry
{
    /// <summary>The <c>Dispatched[P]</c> types made so far, in the order they were made.</summary>
    private readonly List<RecordTypeSymbol> _dispatchedTypes = [];

    /// <summary>The <c>Dispatched[P]</c> types whose routines are registered.</summary>
    private readonly HashSet<RecordTypeSymbol> _dispatchMembersRegistered =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Set once every protocol's routines are resolved (body analysis starts): from then on a new
    /// <c>Dispatched[P]</c> gets its routines when it is made.</summary>
    private bool _dispatchMembersReady;

    /// <summary>Per <c>Dispatched[P]</c> (by its full name), the concrete types the program puts into such a place,
    /// in the order the lowering met them.</summary>
    private readonly Dictionary<string, List<TypeSymbol>> _dispatchArms = new(comparer: StringComparer.Ordinal);

    /// <summary>Per protocol, whether a type that obeys it is a Revare entity (see <see cref="ProtocolHoldsEntities"/>).</summary>
    private readonly Dictionary<ProtocolTypeSymbol, bool> _protocolHoldsEntities =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The routines registered on each <c>Dispatched[P]</c> whose bodies the builder writes, by its full
    /// name.</summary>
    private readonly Dictionary<string, List<RoutineInfo>> _dispatchRoutines = new(comparer: StringComparer.Ordinal);

    /// <summary>The protocol <paramref name="type"/> is the stored value of, when it is a <c>Dispatched[P]</c>.</summary>
    public static ProtocolTypeSymbol? DispatchedProtocol(TypeSymbol? type)
    {
        return type is RecordTypeSymbol
        {
            GenericDefinition.Name: RuntimeContract.Dispatch.Dispatched, TypeArguments: [ProtocolTypeSymbol protocol]
        }
            ? protocol
            : null;
    }

    /// <summary>The protocol as the registry holds it now: resolving a protocol's body puts a new symbol with its
    /// routines in place of the one declared first, which a type made before may still name.</summary>
    public ProtocolTypeSymbol CanonicalProtocol(ProtocolTypeSymbol protocol)
    {
        return LookupType(name: protocol.FullName) as ProtocolTypeSymbol ?? protocol;
    }

    /// <summary>A library routine of <see cref="RuntimeContract.Dispatch"/> by its name.</summary>
    public RoutineInfo? LookupDispatchHelper(string name)
    {
        return LookupRoutine(fullName: $"System.{name}") ?? LookupRoutine(fullName: $"System.Memory.{name}") ??
               LookupRoutine(fullName: name);
    }

    /// <summary>The <c>Dispatched[P]</c> types made so far.</summary>
    public IReadOnlyList<RecordTypeSymbol> DispatchedTypes => _dispatchedTypes;

    /// <summary>Notes a <c>Dispatched[P]</c> just made and, once routines are being resolved, registers its
    /// routines.</summary>
    private void NoteDispatchedType(TypeSymbol resolved)
    {
        if (resolved is not RecordTypeSymbol record || DispatchedProtocol(type: record) == null)
        {
            return;
        }

        _dispatchedTypes.Add(item: record);
        if (_dispatchMembersReady)
        {
            RegisterDispatchMembers(dispatched: record);
        }
    }

    /// <summary>Registers the routines of every <c>Dispatched[P]</c> made so far, and of each one made from now on:
    /// called when the program's routine bodies are about to be analyzed, so every protocol's routines are known.</summary>
    public void PrepareDispatchMembers()
    {
        _dispatchMembersReady = true;
        foreach (RecordTypeSymbol dispatched in _dispatchedTypes.ToList())
        {
            RegisterDispatchMembers(dispatched: dispatched);
        }
    }

    /// <summary>
    /// Whether a value of protocol <paramref name="protocol"/> or of one of its parents (at any depth) is a value of
    /// <paramref name="target"/>: the protocol itself or one it obeys.
    /// </summary>
    public bool ProtocolIncludes(ProtocolTypeSymbol protocol, TypeSymbol? target)
    {
        if (target is not ProtocolTypeSymbol wanted)
        {
            return false;
        }

        var seen = new HashSet<ProtocolTypeSymbol>(comparer: ReferenceEqualityComparer.Instance);
        var stack = new Stack<ProtocolTypeSymbol>();
        stack.Push(item: protocol);
        while (stack.Count > 0)
        {
            ProtocolTypeSymbol current = stack.Pop();
            if (!seen.Add(item: current))
            {
                continue;
            }

            if (ReferenceEquals(objA: current, objB: wanted) || current.FullName == wanted.FullName)
            {
                return true;
            }

            foreach (ProtocolTypeSymbol parent in CanonicalProtocol(protocol: current).ParentProtocols)
            {
                stack.Push(item: parent);
            }
        }

        return false;
    }

    /// <summary>The protocol and each protocol it obeys (at any depth), the protocol first.</summary>
    public List<ProtocolTypeSymbol> ProtocolAndParents(ProtocolTypeSymbol protocol)
    {
        var result = new List<ProtocolTypeSymbol>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var queue = new Queue<ProtocolTypeSymbol>();
        queue.Enqueue(item: protocol);
        while (queue.Count > 0)
        {
            ProtocolTypeSymbol current = queue.Dequeue();
            if (!seen.Add(item: current.FullName))
            {
                continue;
            }

            result.Add(item: current);
            foreach (ProtocolTypeSymbol parent in CanonicalProtocol(protocol: current).ParentProtocols)
            {
                queue.Enqueue(item: parent);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a Revare place of <paramref name="protocol"/> keeps entities: some entity type of the program obeys
    /// it. Such a place has one original, as an entity has: it moves with <c>steal</c>, is never copied by <c>=</c>,
    /// and a read of it gives a token. A record kept there is kept the same way. In a build whose entities are shared
    /// (Mielia) every place is a value: an entity there is its handle.
    /// </summary>
    public bool ProtocolHoldsEntities(ProtocolTypeSymbol protocol)
    {
        if (CompilationRules.EntitiesAreShared)
        {
            return false;
        }

        if (_protocolHoldsEntities.TryGetValue(key: protocol, value: out bool known))
        {
            return known;
        }

        bool holds = false;
        foreach (TypeSymbol type in GetTypesByCategory(category: TypeCategory.Entity).ToList())
        {
            if (type is EntityTypeSymbol { IsGenericDefinition: false } entity &&
                !IsStdlibSourcePath(path: entity.Location?.FileName) &&
                (TypeObeysProtocol(type: entity, protocolName: protocol.Name, resolvedProtocol: protocol) ||
                 HasProtocolRoutines(type: entity, protocol: protocol)))
            {
                holds = true;
                break;
            }
        }

        _protocolHoldsEntities[key: protocol] = holds;
        return holds;
    }

    /// <summary>Whether <paramref name="type"/> has a routine of every name <paramref name="protocol"/> and its
    /// parents ask for (a type obeys a protocol by having its routines).</summary>
    private bool HasProtocolRoutines(TypeSymbol type, ProtocolTypeSymbol protocol)
    {
        HashSet<string> names = CollectProtocolMemberRoutineNames(proto: CanonicalProtocol(protocol: protocol));
        return names.Count > 0 && names.All(predicate: name =>
            GetMemberRoutinesForType(type: type).Any(predicate: r => r.Name == name));
    }

    /// <summary>Whether <paramref name="path"/> is a standard library source file.</summary>
    private static bool IsStdlibSourcePath(string? path)
    {
        return path != null && path.Replace(oldChar: '\\', newChar: '/').Contains(value: "/Standard/");
    }

    /// <summary>
    /// The type hash of <paramref name="type"/> as a stored protocol value: FNV-1a (32 bits) over the UTF-8 bytes of
    /// its name with its module and its type arguments (<see cref="TypeSymbol.FullName"/>), 1 in place of 0. A
    /// Mielia entity's handle (<c>Roamed[E]</c>) has the hash of the entity.
    /// </summary>
    public static uint DispatchTypeHash(TypeSymbol type)
    {
        TypeSymbol named = DispatchArmEntity(type: type) ?? type;
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(s: named.FullName))
        {
            hash = (hash ^ b) * 16777619;
        }

        return hash == 0
            ? 1
            : hash;
    }

    /// <summary>The entity a Mielia handle <c>Roamed[E]</c> stands for, or null.</summary>
    public static EntityTypeSymbol? DispatchArmEntity(TypeSymbol type)
    {
        return type is RecordTypeSymbol
        {
            GenericDefinition.Name: RuntimeContract.Roamed, TypeArguments: [EntityTypeSymbol entity]
        }
            ? entity
            : null;
    }

    /// <summary>Notes that the program puts a value of <paramref name="concrete"/> into a place of
    /// <paramref name="dispatched"/>.</summary>
    public void NoteDispatchArm(TypeSymbol dispatched, TypeSymbol concrete)
    {
        if (!_dispatchArms.TryGetValue(key: dispatched.FullName, value: out List<TypeSymbol>? arms))
        {
            arms = [];
            _dispatchArms[key: dispatched.FullName] = arms;
        }

        if (arms.All(predicate: a => a.FullName != concrete.FullName))
        {
            arms.Add(item: concrete);
        }
    }

    /// <summary>The concrete types the program puts into places of <paramref name="dispatched"/>.</summary>
    public IReadOnlyList<TypeSymbol> DispatchArms(TypeSymbol dispatched)
    {
        return _dispatchArms.TryGetValue(key: dispatched.FullName, value: out List<TypeSymbol>? arms)
            ? arms
            : [];
    }

    /// <summary>The routines registered on <paramref name="dispatched"/> whose bodies the builder writes.</summary>
    public IReadOnlyList<RoutineInfo> DispatchRoutines(TypeSymbol dispatched)
    {
        return _dispatchRoutines.TryGetValue(key: dispatched.FullName, value: out List<RoutineInfo>? routines)
            ? routines
            : [];
    }

    /// <summary>
    /// Registers on <paramref name="dispatched"/> a routine for each routine of its protocol and the protocols the
    /// protocol obeys that a value of unknown type can take (no parameter or result of the protocol's own type
    /// <c>Me</c>), its <c>destruct</c>, and, when the place is a value (<see cref="ProtocolHoldsEntities"/> false),
    /// its <c>share</c>. Their bodies switch on the type hash and are written once the stored types are known.
    /// </summary>
    private void RegisterDispatchMembers(RecordTypeSymbol dispatched)
    {
        if (!_dispatchMembersRegistered.Add(item: dispatched) ||
            DispatchedProtocol(type: dispatched) is not { } protocol)
        {
            return;
        }

        var registered = new List<RoutineInfo>();
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (ProtocolTypeSymbol owner in ProtocolAndParents(protocol: CanonicalProtocol(protocol: protocol)))
        {
            foreach (ProtocolMemberRoutineInfo member in owner.MemberRoutines)
            {
                if (!member.IsInstanceMemberRoutine || !names.Add(item: member.Name) ||
                    member.ParameterTypes.Any(predicate: HoldsProtocolSelf) ||
                    member.ReturnType is { } selfReturn && HoldsProtocolSelf(type: selfReturn))
                {
                    continue;
                }

                bool readOnly = member.Mutation == MutationCategory.Readonly;
                var parameters = new List<ParamInfo>();
                for (int i = 0; i < member.ParameterTypes.Count; i++)
                {
                    string name = i < member.ParameterNames.Count
                        ? member.ParameterNames[index: i]
                        : $"arg{i}";
                    parameters.Add(item: new ParamInfo(name: name, type: member.ParameterTypes[index: i]) { Index = i });
                }

                var routine = new RoutineInfo(name: member.Name)
                {
                    Kind = RoutineKind.MemberRoutine,
                    OwnerType = dispatched,
                    Parameters = parameters,
                    ReturnType = member.ReturnType ?? LookupType(name: "None"),
                    IsFailable = member.IsFailable,
                    DeclaredMutation = member.Mutation,
                    MutationCategory = member.Mutation,
                    Location = dispatched.GenericDefinition?.Location ?? dispatched.Location,
                    Module = dispatched.Module,
                    Annotations = readOnly
                        ? ["readonly"]
                        : [],
                    IsSynthesized = true
                };
                RegisterRoutine(routine: routine);
                registered.Add(item: routine);
            }
        }

        TypeSymbol? none = LookupType(name: "None");
        var destruct = new RoutineInfo(name: RuntimeContract.Destroy)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = dispatched,
            ReturnType = none,
            Location = dispatched.GenericDefinition?.Location ?? dispatched.Location,
            Module = dispatched.Module,
            Visibility = SyntaxTree.VisibilityModifier.Secret,
            Annotations = ["override"],
            IsSynthesized = true
        };
        RegisterRoutine(routine: destruct);
        registered.Add(item: destruct);

        if (!ProtocolHoldsEntities(protocol: protocol))
        {
            var share = new RoutineInfo(name: RuntimeContract.Duplication.Share)
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = dispatched,
                ReturnType = dispatched,
                Location = dispatched.GenericDefinition?.Location ?? dispatched.Location,
                Module = dispatched.Module,
                Visibility = SyntaxTree.VisibilityModifier.Secret,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Annotations = ["readonly", "override"],
                IsSynthesized = true
            };
            RegisterRoutine(routine: share);
            registered.Add(item: share);
        }

        _dispatchRoutines[key: dispatched.FullName] = registered;
    }

    private static bool HoldsProtocolSelf(TypeSymbol type)
    {
        return type is ProtocolSelfTypeSymbol ||
               (type.TypeArguments ?? []).Any(predicate: HoldsProtocolSelf);
    }
}
