namespace ExamPrism.Tests.Domain;

using System.Linq.Expressions;
using Common.Domain;
using Common.Domain.Specifications;
using FluentAssertions;
using Xunit;

public class DddPrimitivesTests
{
    private sealed class SampleEntity : Entity<Guid>
    {
        public SampleEntity(Guid id) : base(id) { }
        public SampleEntity() { }
    }

    private sealed class OtherEntity : Entity<Guid>
    {
        public OtherEntity(Guid id) : base(id) { }
    }

    private sealed class Address : ValueObject
    {
        public string Street { get; }
        public string? City { get; }

        public Address(string street, string? city)
        {
            Street = street;
            City = city;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Street;
            yield return City;
        }
    }

    private sealed class Money : ValueObject
    {
        public decimal Amount { get; }
        public string Currency { get; }

        public Money(decimal amount, string currency)
        {
            Amount = amount;
            Currency = currency;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    private sealed record TestDomainEvent(string Payload) : DomainEventBase;

    private sealed class SampleAggregate : AggregateRoot<Guid>, IAuditableEntity
    {
        public DateTimeOffset CreatedAt { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }

        public SampleAggregate(Guid id) : base(id) { }

        public void DoSomething(string message)
        {
            RaiseDomainEvent(new TestDomainEvent(message));
        }

        public void SetCreated(DateTimeOffset now) => CreatedAt = now;
        public void SetUpdated(DateTimeOffset now) => UpdatedAt = now;
    }

    private sealed class ProductItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public decimal Price { get; set; }
        public int Score { get; set; }
    }

    private sealed class ActiveItemSpec : Specification<ProductItem>
    {
        public ActiveItemSpec()
        {
            Where(x => x.IsActive);
        }
    }

    private sealed class MinScoreSpec : Specification<ProductItem>
    {
        public MinScoreSpec(int minScore)
        {
            Where(x => x.Score >= minScore);
        }
    }

    private sealed class ProductDtoSpec : Specification<ProductItem, string>
    {
        public ProductDtoSpec()
        {
            Where(x => x.IsActive);
            ApplyOrderBy(x => x.Price);
            ApplyPaging(skip: 10, take: 5);
            WithSplitQuery();
            WithIgnoreQueryFilters();
            Select(x => x.Name);
        }
    }

    [Fact]
    public void Entity_Equality_ComparesByIdAndExactType_AndTreatsTransientDefaultIdsAsDistinctUnlessSameReference()
    {
        var id = Guid.NewGuid();
        var entity1 = new SampleEntity(id);
        var entity2 = new SampleEntity(id);
        var differentIdEntity = new SampleEntity(Guid.NewGuid());
        var otherTypeSameId = new OtherEntity(id);

        entity1.Should().Be(entity2);
        (entity1 == entity2).Should().BeTrue();
        (entity1 != differentIdEntity).Should().BeTrue();
        entity1.GetHashCode().Should().Be(entity2.GetHashCode());

        entity1.Equals(otherTypeSameId).Should().BeFalse();
        (entity1 != otherTypeSameId).Should().BeTrue();

        var transient1 = new SampleEntity(Guid.Empty);
        var transient2 = new SampleEntity(Guid.Empty);
        transient1.IsTransient().Should().BeTrue();
        transient2.IsTransient().Should().BeTrue();
        (transient1 == transient2).Should().BeFalse();
        transient1.Equals(transient2).Should().BeFalse();

        (transient1 == transient1).Should().BeTrue();
    }

    [Fact]
    public void ValueObject_Equality_ComparesAllComponents_HandlesNulls_AndRejectsDifferentDerivedTypes()
    {
        var m1 = new Money(100, "USD");
        var m2 = new Money(100, "USD");
        var m3 = new Money(200, "USD");

        m1.Should().Be(m2);
        (m1 == m2).Should().BeTrue();
        (m1 != m3).Should().BeTrue();
        m1.GetHashCode().Should().Be(m2.GetHashCode());

        var a1 = new Address("Main St", null);
        var a2 = new Address("Main St", null);
        var a3 = new Address("Main St", "NY");

        a1.Should().Be(a2);
        (a1 == a2).Should().BeTrue();
        (a1 != a3).Should().BeTrue();

        var act = () => a1.GetHashCode();
        act.Should().NotThrow();
        a1.GetHashCode().Should().Be(a2.GetHashCode());

        m1.Equals(a1).Should().BeFalse();
    }

    [Fact]
    public void AggregateRoot_RaiseAndDequeueDomainEvents_ReturnsSnapshotInFifoOrderAndClearsInternalQueue()
    {
        var agg = new SampleAggregate(Guid.NewGuid());
        agg.DomainEvents.Should().BeEmpty();

        agg.DoSomething("event 1");
        agg.DoSomething("event 2");

        agg.DomainEvents.Should().HaveCount(2);

        var dequeued = agg.DequeueDomainEvents();
        dequeued.Should().HaveCount(2);
        ((TestDomainEvent)dequeued[0]).Payload.Should().Be("event 1");
        ((TestDomainEvent)dequeued[1]).Payload.Should().Be("event 2");

        agg.DomainEvents.Should().BeEmpty();
        agg.DequeueDomainEvents().Should().BeEmpty();

        agg.DoSomething("event 3");
        agg.ClearDomainEvents();
        agg.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Specification_IsSatisfiedBy_AndBooleanCombinators_AndOrNot_EvaluateCorrectlyInMemory()
    {
        var activeSpec = new ActiveItemSpec();
        var scoreSpec = new MinScoreSpec(50);

        var activeAndHigh = activeSpec.And(scoreSpec);
        var activeOrHigh = activeSpec.Or(scoreSpec);
        var notActive = activeSpec.Not();

        var item1 = new ProductItem { IsActive = true, Score = 60 };
        var item2 = new ProductItem { IsActive = true, Score = 30 };
        var item3 = new ProductItem { IsActive = false, Score = 80 };
        var item4 = new ProductItem { IsActive = false, Score = 10 };

        activeAndHigh.IsSatisfiedBy(item1).Should().BeTrue();
        activeAndHigh.IsSatisfiedBy(item2).Should().BeFalse();
        activeAndHigh.IsSatisfiedBy(item3).Should().BeFalse();

        activeOrHigh.IsSatisfiedBy(item1).Should().BeTrue();
        activeOrHigh.IsSatisfiedBy(item2).Should().BeTrue();
        activeOrHigh.IsSatisfiedBy(item3).Should().BeTrue();
        activeOrHigh.IsSatisfiedBy(item4).Should().BeFalse();

        notActive.IsSatisfiedBy(item1).Should().BeFalse();
        notActive.IsSatisfiedBy(item4).Should().BeTrue();

        var matchAllSpec = new MatchAllProductSpec();
        var orMatchAll = activeSpec.Or(matchAllSpec);
        orMatchAll.Criteria.Should().BeNull();
        orMatchAll.IsSatisfiedBy(item4).Should().BeTrue();

        var matchNoneSpec = matchAllSpec.Not();
        matchNoneSpec.IsSatisfiedBy(item1).Should().BeFalse();
        matchNoneSpec.IsSatisfiedBy(item4).Should().BeFalse();

        var dtoSpec = new ProductDtoSpec();
        dtoSpec.Skip.Should().Be(10);
        dtoSpec.Take.Should().Be(5);
        dtoSpec.AsSplitQuery.Should().BeTrue();
        dtoSpec.IgnoreQueryFilters.Should().BeTrue();
        dtoSpec.OrderExpressions.Should().ContainSingle(o => !o.Descending);
        dtoSpec.Selector.Should().NotBeNull();
    }

    private sealed class MatchAllProductSpec : Specification<ProductItem>
    {
    }
}

