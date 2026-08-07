namespace Gitfs.Core;

/// <summary>Номера совпадают с кодами типов в packfile (план M1b).</summary>
public enum GitObjectType
{
    Commit = 1,
    Tree = 2,
    Blob = 3,
    Tag = 4,
}
