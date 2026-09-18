using CvManager.Data;
using CvManager.Models;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Services;

// Decides which positions a candidate may build a CV for.
//
// A position is either public, or it carries a list of rules ("IELTS Score > 7",
// "Remote Work is checked") and the candidate must satisfy every one of them.
// The whole check is written as one LINQ expression so it runs as a single SQL
// statement with EXISTS sub-queries - evaluating rules in C# would mean loading
// every position and every attribute value of the user first.
public class PositionAccessService
{
    private readonly ApplicationDbContext _db;

    public PositionAccessService(ApplicationDbContext db)
    {
        _db = db;
    }

    // The positions this candidate can create a CV for. Returns IQueryable on
    // purpose: callers add their own paging, sorting and projection, so only the
    // columns and rows actually shown are fetched.
    public IQueryable<Position> AccessibleTo(string userId)
    {
        // Only answered values count. An unanswered attribute satisfies no rule,
        // including the negative ones - "Remote Work is not checked" means the
        // candidate said no, not that they never filled it in.
        var answered = _db.AttributeValues.Where(v => v.UserId == userId && v.HasValue);

        return _db.Positions.Where(p =>
            p.IsPublic ||
            p.AccessRules.All(rule => answered.Any(v =>
                v.AttributeId == rule.AttributeId &&
                (
                    // "has any value at all"
                    (rule.Operator == RuleOperator.IsFilled)

                    // Boolean
                    || (rule.Operator == RuleOperator.IsChecked && v.ValueBoolean == true)
                    || (rule.Operator == RuleOperator.IsNotChecked && v.ValueBoolean == false)

                    // Equality across the typed columns. Only the column that
                    // matches the attribute's type is filled on both sides, so
                    // the other comparisons are simply false.
                    || (rule.Operator == RuleOperator.Equals && (
                        (rule.ValueString != null && v.ValueString == rule.ValueString)
                        || (rule.ValueNumber != null && v.ValueNumber == rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate == rule.ValueDate)
                        || (rule.ValueBoolean != null && v.ValueBoolean == rule.ValueBoolean)
                        || (rule.ValueOptionId != null && v.ValueOptionId == rule.ValueOptionId)))

                    || (rule.Operator == RuleOperator.NotEquals && (
                        (rule.ValueString != null && v.ValueString != rule.ValueString)
                        || (rule.ValueNumber != null && v.ValueNumber != rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate != rule.ValueDate)
                        || (rule.ValueBoolean != null && v.ValueBoolean != rule.ValueBoolean)
                        || (rule.ValueOptionId != null && v.ValueOptionId != rule.ValueOptionId)))

                    // Ordering, for numbers and dates.
                    || (rule.Operator == RuleOperator.GreaterThan && (
                        (rule.ValueNumber != null && v.ValueNumber > rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate > rule.ValueDate)))

                    || (rule.Operator == RuleOperator.GreaterOrEqual && (
                        (rule.ValueNumber != null && v.ValueNumber >= rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate >= rule.ValueDate)))

                    || (rule.Operator == RuleOperator.LessThan && (
                        (rule.ValueNumber != null && v.ValueNumber < rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate < rule.ValueDate)))

                    || (rule.Operator == RuleOperator.LessOrEqual && (
                        (rule.ValueNumber != null && v.ValueNumber <= rule.ValueNumber)
                        || (rule.ValueDate != null && v.ValueDate <= rule.ValueDate)))

                    // Substring match for text attributes.
                    || (rule.Operator == RuleOperator.Contains
                        && rule.ValueString != null
                        && v.ValueString != null
                        && EF.Functions.ILike(v.ValueString, "%" + rule.ValueString + "%"))
                ))));
    }

    public Task<bool> CanAccessAsync(int positionId, string userId)
    {
        return AccessibleTo(userId).AnyAsync(p => p.Id == positionId);
    }

    // Which operators the UI offers for a given attribute type. Kept here next to
    // the evaluation above so the two cannot drift apart.
    public static RuleOperator[] OperatorsFor(AttributeType type) => type switch
    {
        AttributeType.Boolean =>
            [RuleOperator.IsChecked, RuleOperator.IsNotChecked],

        AttributeType.Numeric or AttributeType.Date =>
            [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.GreaterThan,
             RuleOperator.GreaterOrEqual, RuleOperator.LessThan, RuleOperator.LessOrEqual,
             RuleOperator.IsFilled],

        AttributeType.Dropdown =>
            [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.IsFilled],

        AttributeType.String or AttributeType.Text =>
            [RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.Contains,
             RuleOperator.IsFilled],

        // Period and Image only support "did they fill it in".
        _ => [RuleOperator.IsFilled]
    };
}
