namespace SystemXTransMedExamples.SystemXAPI
{
    /// <summary>
    /// Serialiseres til JSON over linja, EasyNetQ gjør dette for oss.
    /// </summary>
    public class OpenPatientJournalCommand
    {
        public string NIN;
        public uint WorkingPosition;
    }
}