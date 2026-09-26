using LLMQuorum.Core.Configuration;

namespace LLMQuorum.Core.Sweep;

/// <summary>Builds a <see cref="QuestionSheet"/> from configuration: judges, judgement store, current seats.</summary>
public static class QuestionSheetFactory
{
    #region Public Methods

    /// <summary>Creates the sheet generator.</summary>
    /// <param name="config">Resolved configuration.</param>
    /// <returns>A ready generator.</returns>
    /// <exception cref="InvalidDataException">A configured judge seat is not in profiles.json.</exception>
    public static QuestionSheet Create( QuorumConfig config )
    {
        var profiles = ProfileCatalog.Load( config.ResolvePath( config.ProfilesPath ) );
        var caller = new ProfileCaller( config.KeysDirectory, config.ConfigDirectory, new QuotaLedger( config.ConnectionString ) );

        var judges = config.SheetJudges.Select( seatId =>
        {
            var profile = profiles.FirstOrDefault( p => p.SeatId == seatId )
                          ?? throw new InvalidDataException( $"SheetJudges seat '{seatId}' is not in profiles.json." );
            return (IAnswerJudge)new ProfileJudge( caller, profile );
        } ).ToList();

        var panel = new JudgePanel( judges, new SqlJudgementStore( config.ConnectionString ) );
        // Only enabled seats are shown: a disabled seat (e.g. marked not responsive) is never asked again and its
        // old failures are not counted; the sheet names it on the Excluded line instead.
        return new QuestionSheet( config.ConnectionString, panel,
                                  profiles.Where( p => p.Enabled ).Select( p => p.SeatId ).ToHashSet( StringComparer.Ordinal ) );
    }

    #endregion Public Methods
}
