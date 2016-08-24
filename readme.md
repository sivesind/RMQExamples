## Hensikt

Kodeeksempler for integrasjon mellom TransMed og SystemX. Koden bruker EasyNetQ rammeverk for å forenkle bruk av RabbitMQ, men konseptene stort sett språkuavhengig. Ref:
https://www.rabbitmq.com/tutorials/tutorial-six-dotnet.html

## Hvordan teste?

Åpne solution i Visual Studio (^2015), bygg prosjektet og kjør enhetstester i klassen MessagingExamples.cs. (avhengigheter lastes ned med nuget)
Du må også ha en testrunner for NUnit, f.eks. 
- ReSharper: https://www.jetbrains.com/resharper/
- NUnit Test Adapter for Visual Studio: http://nunit.org/index.php?p=vsTestAdapter&r=2.6.4

(eller bare endre koden til å kjøre fra en Main-klasse :-))

## Kontaktinfo

Lars Eirik Sivesind, Systemarkitekt, Locus Public Safety AS, les@locus.no, 95438171