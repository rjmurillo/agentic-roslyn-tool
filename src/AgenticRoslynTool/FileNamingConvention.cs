namespace AgenticRoslynTool;

/// <summary>
/// Which StyleCop SA1649 file naming convention the planned file names follow. Mirrors the
/// <c>fileNamingConvention</c> setting in <c>stylecop.json</c>, which the tool never reads:
/// the caller states the convention with <c>--file-naming</c>.
/// </summary>
internal enum FileNamingConvention
{
    /// <summary>
    /// The default. A generic type is named <c>Name{T1,T2}.cs</c>, and only when another
    /// type in the same input shares its simple name. Otherwise it is named <c>Name.cs</c>.
    /// </summary>
    StyleCop,

    /// <summary>
    /// Every generic type is named <c>Name`N.cs</c>, where N is the number of type
    /// parameters. A non-generic type is named <c>Name.cs</c>.
    /// </summary>
    Metadata,
}
