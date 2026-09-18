namespace CvManager.Models;

// The eight attribute types from the requirements. The type decides which
// "Value*" column of AttributeValue is used and which operators the access
// rules can offer for that attribute.
public enum AttributeType
{
    String = 0,
    Text = 1,
    Image = 2,
    Numeric = 3,
    Date = 4,
    Period = 5,
    Boolean = 6,
    Dropdown = 7
}

// Operators for position access rules. Which of these is allowed depends on the
// attribute type (see AttributeTypeRules) - e.g. GreaterThan only makes sense
// for Numeric and Date, IsChecked only for Boolean.
public enum RuleOperator
{
    Equals = 0,
    NotEquals = 1,
    GreaterThan = 2,
    GreaterOrEqual = 3,
    LessThan = 4,
    LessOrEqual = 5,
    Contains = 6,
    IsChecked = 7,
    IsNotChecked = 8,
    IsFilled = 9
}

// A CV is a draft until the candidate presses Publish, and Publish is only
// allowed when every attribute of the position has a value. Recruiters only
// see published CVs.
public enum CvState
{
    Draft = 0,
    Published = 1
}

// Optional extra on positions, used for filtering/sorting the positions table.
public enum PositionLevel
{
    Junior = 0,
    Middle = 1,
    Senior = 2,
    CLevel = 3
}
