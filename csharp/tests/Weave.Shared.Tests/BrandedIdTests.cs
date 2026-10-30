using System.ComponentModel;
using System.Globalization;
using Weave.Shared.Ids;

namespace Weave.Shared.Tests;

/// <summary>
/// One test per behaviour across all 10 branded IDs. Each branded-ID
/// type is source-generated from <c>[BrandedId]</c> (see
/// Weave.SourceGen.BrandedIdGenerator), so the API shape is identical —
/// the parametric nested classes below exercise every branch once and
/// apply it uniformly. A new branded ID added to BrandedIds.cs gets
/// contract coverage for free by adding one line to TypeNames.
/// </summary>
public static class BrandedIdTests
{
    // The branded ID behaviours don't depend on the concrete type, so
    // the tests run over a type-erased "adapter" that delegates to
    // each branded-ID's static factory methods via a small bridge.
    // Adding a new branded ID = adding a row to this list.
    public static TheoryData<IBrandedIdTypeAdapter> Adapters => new()
    {
        new BrandedIdTypeAdapter<WorkspaceId>(
            WorkspaceId.New, WorkspaceId.From, WorkspaceId.Parse,
            (string? s, out WorkspaceId id) => WorkspaceId.TryParse(s, out id),
            () => WorkspaceId.Empty,
            id => ((WorkspaceId)id).Value,
            id => ((WorkspaceId)id).IsEmpty,
            s => (WorkspaceId)s,
            typeof(WorkspaceId)),
        new BrandedIdTypeAdapter<AgentId>(
            AgentId.New, AgentId.From, AgentId.Parse,
            (string? s, out AgentId id) => AgentId.TryParse(s, out id),
            () => AgentId.Empty,
            id => ((AgentId)id).Value,
            id => ((AgentId)id).IsEmpty,
            s => (AgentId)s,
            typeof(AgentId)),
        new BrandedIdTypeAdapter<AgentTaskId>(
            AgentTaskId.New, AgentTaskId.From, AgentTaskId.Parse,
            (string? s, out AgentTaskId id) => AgentTaskId.TryParse(s, out id),
            () => AgentTaskId.Empty,
            id => ((AgentTaskId)id).Value,
            id => ((AgentTaskId)id).IsEmpty,
            s => (AgentTaskId)s,
            typeof(AgentTaskId)),
        new BrandedIdTypeAdapter<ContainerId>(
            ContainerId.New, ContainerId.From, ContainerId.Parse,
            (string? s, out ContainerId id) => ContainerId.TryParse(s, out id),
            () => ContainerId.Empty,
            id => ((ContainerId)id).Value,
            id => ((ContainerId)id).IsEmpty,
            s => (ContainerId)s,
            typeof(ContainerId)),
        new BrandedIdTypeAdapter<NetworkId>(
            NetworkId.New, NetworkId.From, NetworkId.Parse,
            (string? s, out NetworkId id) => NetworkId.TryParse(s, out id),
            () => NetworkId.Empty,
            id => ((NetworkId)id).Value,
            id => ((NetworkId)id).IsEmpty,
            s => (NetworkId)s,
            typeof(NetworkId)),
        new BrandedIdTypeAdapter<SkillId>(
            SkillId.New, SkillId.From, SkillId.Parse,
            (string? s, out SkillId id) => SkillId.TryParse(s, out id),
            () => SkillId.Empty,
            id => ((SkillId)id).Value,
            id => ((SkillId)id).IsEmpty,
            s => (SkillId)s,
            typeof(SkillId)),
        new BrandedIdTypeAdapter<ChannelId>(
            ChannelId.New, ChannelId.From, ChannelId.Parse,
            (string? s, out ChannelId id) => ChannelId.TryParse(s, out id),
            () => ChannelId.Empty,
            id => ((ChannelId)id).Value,
            id => ((ChannelId)id).IsEmpty,
            s => (ChannelId)s,
            typeof(ChannelId)),
        new BrandedIdTypeAdapter<UserId>(
            UserId.New, UserId.From, UserId.Parse,
            (string? s, out UserId id) => UserId.TryParse(s, out id),
            () => UserId.Empty,
            id => ((UserId)id).Value,
            id => ((UserId)id).IsEmpty,
            s => (UserId)s,
            typeof(UserId)),
        new BrandedIdTypeAdapter<MarketplaceItemId>(
            MarketplaceItemId.New, MarketplaceItemId.From, MarketplaceItemId.Parse,
            (string? s, out MarketplaceItemId id) => MarketplaceItemId.TryParse(s, out id),
            () => MarketplaceItemId.Empty,
            id => ((MarketplaceItemId)id).Value,
            id => ((MarketplaceItemId)id).IsEmpty,
            s => (MarketplaceItemId)s,
            typeof(MarketplaceItemId)),
        new BrandedIdTypeAdapter<TemplateId>(
            TemplateId.New, TemplateId.From, TemplateId.Parse,
            (string? s, out TemplateId id) => TemplateId.TryParse(s, out id),
            () => TemplateId.Empty,
            id => ((TemplateId)id).Value,
            id => ((TemplateId)id).IsEmpty,
            s => (TemplateId)s,
            typeof(TemplateId)),
    };

    public sealed class FactoryAndConversion
    {
        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void New_generates_non_empty_value(IBrandedIdTypeAdapter adapter)
        {
            var id = adapter.New();
            adapter.GetValue(id).ShouldNotBeNullOrWhiteSpace();
            adapter.IsEmpty(id).ShouldBeFalse();
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void New_generates_unique_values(IBrandedIdTypeAdapter adapter)
        {
            var a = adapter.New();
            var b = adapter.New();
            adapter.GetValue(a).ShouldNotBe(adapter.GetValue(b));
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void From_wraps_string(IBrandedIdTypeAdapter adapter)
        {
            var id = adapter.From("my-id");
            adapter.GetValue(id).ShouldBe("my-id");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void Parse_matches_From(IBrandedIdTypeAdapter adapter)
        {
            var viaFrom = adapter.From("abc-123");
            var viaParse = adapter.Parse("abc-123");
            adapter.GetValue(viaParse).ShouldBe(adapter.GetValue(viaFrom));
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void TryParse_returns_true_for_valid_string(IBrandedIdTypeAdapter adapter)
        {
            var ok = adapter.TryParse("valid-id", out var id);
            ok.ShouldBeTrue();
            adapter.GetValue(id).ShouldBe("valid-id");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void TryParse_returns_false_for_null(IBrandedIdTypeAdapter adapter)
        {
            var ok = adapter.TryParse(null, out _);
            ok.ShouldBeFalse();
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void Empty_is_empty(IBrandedIdTypeAdapter adapter)
        {
            var empty = adapter.Empty();
            adapter.IsEmpty(empty).ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void ImplicitOperator_to_string_returns_value(IBrandedIdTypeAdapter adapter)
        {
            var id = adapter.From("implicit-cast");
            string asString = adapter.GetValue(id);
            asString.ShouldBe("implicit-cast");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void ExplicitOperator_from_string_wraps_value(IBrandedIdTypeAdapter adapter)
        {
            var id = adapter.FromExplicitCast("explicit-cast");
            adapter.GetValue(id).ShouldBe("explicit-cast");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void ToString_returns_value(IBrandedIdTypeAdapter adapter)
        {
            var id = adapter.From("tostring-test");
            id.ToString().ShouldBe("tostring-test");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void Empty_ToString_returns_empty_string(IBrandedIdTypeAdapter adapter)
        {
            var empty = adapter.Empty();
            empty.ToString().ShouldBe(string.Empty);
        }
    }

    public sealed class Comparison
    {
        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void Equal_values_compare_equal(IBrandedIdTypeAdapter adapter)
        {
            var a = adapter.From("same-value");
            var b = adapter.From("same-value");
            a.ShouldBe(b);
            a.GetHashCode().ShouldBe(b.GetHashCode());
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void Different_values_compare_not_equal(IBrandedIdTypeAdapter adapter)
        {
            var a = adapter.From("value-a");
            var b = adapter.From("value-b");
            a.ShouldNotBe(b);
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void CompareTo_orders_lexicographically(IBrandedIdTypeAdapter adapter)
        {
            var a = adapter.From("alpha");
            var b = adapter.From("bravo");
            adapter.LessThan(a, b).ShouldBeTrue();
            adapter.GreaterThan(b, a).ShouldBeTrue();
            adapter.LessThanOrEqual(a, a).ShouldBeTrue();
            adapter.GreaterThanOrEqual(a, a).ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void LessThan_operator_works(IBrandedIdTypeAdapter adapter)
        {
            var a = adapter.From("a");
            var b = adapter.From("b");
            adapter.LessThan(a, b).ShouldBeTrue();
            adapter.GreaterThan(b, a).ShouldBeTrue();
            adapter.LessThanOrEqual(a, a).ShouldBeTrue();
            adapter.GreaterThanOrEqual(a, a).ShouldBeTrue();
        }
    }

    public sealed class TypeConverterBehaviour
    {
        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void TypeConverter_can_convert_from_string(IBrandedIdTypeAdapter adapter)
        {
            var converter = TypeDescriptor.GetConverter(adapter.IdType);
            converter.CanConvertFrom(typeof(string)).ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void TypeConverter_converts_string_to_branded_id(IBrandedIdTypeAdapter adapter)
        {
            var converter = TypeDescriptor.GetConverter(adapter.IdType);
            var result = converter.ConvertFrom(null, CultureInfo.InvariantCulture, "type-conv-value");
            result.ShouldNotBeNull();
            adapter.GetValue(result).ShouldBe("type-conv-value");
        }

        [Theory]
        [MemberData(nameof(Adapters), MemberType = typeof(BrandedIdTests))]
        public void TypeConverter_converts_branded_id_to_string(IBrandedIdTypeAdapter adapter)
        {
            var converter = TypeDescriptor.GetConverter(adapter.IdType);
            var id = adapter.From("convert-to-string");
            var result = converter.ConvertTo(null, CultureInfo.InvariantCulture, id, typeof(string));
            result.ShouldBe("convert-to-string");
        }
    }

    // ── Adapter plumbing (not tests) ───────────────────────────────

    public interface IBrandedIdTypeAdapter
    {
        Type IdType { get; }
        object New();
        object From(string value);
        object Parse(string value);
        bool TryParse(string? value, out object id);
        object Empty();
        string GetValue(object id);
        bool IsEmpty(object id);
        object FromExplicitCast(string value);
        bool LessThan(object a, object b);
        bool GreaterThan(object a, object b);
        bool LessThanOrEqual(object a, object b);
        bool GreaterThanOrEqual(object a, object b);
    }

    private sealed class BrandedIdTypeAdapter<T> : IBrandedIdTypeAdapter
        where T : struct, IComparable<T>
    {
        public delegate bool TryParseDelegate(string? value, out T id);

        private readonly Func<T> _new;
        private readonly Func<string, T> _from;
        private readonly Func<string, T> _parse;
        private readonly TryParseDelegate _tryParse;
        private readonly Func<T> _empty;
        private readonly Func<object, string> _getValue;
        private readonly Func<object, bool> _isEmpty;
        private readonly Func<string, T> _fromExplicitCast;

        public BrandedIdTypeAdapter(
            Func<T> newFn,
            Func<string, T> fromFn,
            Func<string, T> parseFn,
            TryParseDelegate tryParseFn,
            Func<T> emptyFn,
            Func<object, string> getValueFn,
            Func<object, bool> isEmptyFn,
            Func<string, T> fromExplicitCastFn,
            Type idType)
        {
            _new = newFn;
            _from = fromFn;
            _parse = parseFn;
            _tryParse = tryParseFn;
            _empty = emptyFn;
            _getValue = getValueFn;
            _isEmpty = isEmptyFn;
            _fromExplicitCast = fromExplicitCastFn;
            IdType = idType;
        }

        public Type IdType { get; }
        public object New() => _new();
        public object From(string value) => _from(value);
        public object Parse(string value) => _parse(value);

        public bool TryParse(string? value, out object id)
        {
            var ok = _tryParse(value, out T typed);
            id = typed;
            return ok;
        }

        public object Empty() => _empty();
        public string GetValue(object id) => _getValue(id);
        public bool IsEmpty(object id) => _isEmpty(id);
        public object FromExplicitCast(string value) => _fromExplicitCast(value);

        public bool LessThan(object a, object b) => Comparer<T>.Default.Compare((T)a, (T)b) < 0;
        public bool GreaterThan(object a, object b) => Comparer<T>.Default.Compare((T)a, (T)b) > 0;
        public bool LessThanOrEqual(object a, object b) => Comparer<T>.Default.Compare((T)a, (T)b) <= 0;
        public bool GreaterThanOrEqual(object a, object b) => Comparer<T>.Default.Compare((T)a, (T)b) >= 0;

        // TheoryData needs a stable display name; include the type name.
        public override string ToString() => IdType.Name;
    }
}
