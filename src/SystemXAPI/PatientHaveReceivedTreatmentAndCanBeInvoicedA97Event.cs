namespace SystemXTransMedExamples.SystemXAPI
{
    /// <summary>
    /// Serialiseres til JSON over linja, EasyNetQ gjør dette for oss.
    /// </summary>
    public class PatientHaveReceivedTreatmentAndCanBeInvoicedA97Event
    {
        public string NIN;
        public string TreatmentDescription;
        public string UserId;
    }
}