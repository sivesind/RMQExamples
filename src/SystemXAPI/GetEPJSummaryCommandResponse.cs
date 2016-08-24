namespace SystemXTransMedExamples.SystemXAPI
{
    /// <summary>
    /// Serialiseres til JSON over linja, EasyNetQ gjør dette for oss.
    /// </summary>
    public class GetEPJSummaryCommandResponse
    {
        public string NIN;
        public string PatientData1;
        public string PatientData2;
        public string PatientData3;
    }
}