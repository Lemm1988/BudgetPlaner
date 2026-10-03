# Entscheidungen, Inkonsistenzen & fachliche Regeln

Dieses Dokument hält fest, wie Lücken und Widersprüche im Lastenheft aufgelöst wurden und welche
Rechenregeln die App verwendet. Es ist die verbindliche Fachspezifikation für den Code
(`src/BudgetPlaner.Domain`).

## 1. Entscheidungen des Auftraggebers (2026-10-01)

| ID | Thema | Entscheidung |
|----|-------|--------------|
| D-01 | Kategorienmodell | **50/30/20-Hierarchie**: Oberkategorien *Bedarf*, *Wunsch*, *Sparen* (+ *Einnahmen*, *Sonstiges*); die BudgetHeld-Bereiche liegen als Unterkategorien darunter: Bedarf → Wohnen, Mobilität, Lebenshaltung · Wunsch → Guilt Free, Geben · Sparen → Sparen, Investieren, Schuldenabbau. |
| D-02 | Ist-Werte (F-066, F-073) | **Abhaken + Ist-Betrag**: Jede Position kann pro Monat als *bezahlt/erhalten* abgehakt werden; optional wird ein abweichender Ist-Betrag erfasst. Ohne Eintrag gilt **Ist = Soll**. Beim Monatsabschluss werden offene Ist-Werte abgefragt. |

## 2. Inkonsistenzen im Lastenheft und ihre Auflösung

| ID | Fundstelle | Problem | Auflösung |
|----|-----------|---------|-----------|
| I-01 | DoD vs. F-Liste | „Monat abschließen“ wird in der Definition of Done verlangt, aber keine F-Anforderung beschreibt es. | Funktion **Monatsabschluss** (siehe §4.6): Ist-Werte bestätigen, Rest-Unverplant übertragen oder einem Polster zuweisen, optional Kontostand erfassen, Monat als `MonthSnapshot` einfrieren; Wiedereröffnen möglich. |
| I-02 | F-013 vs. F-061/F-070/F-074 | Jährliche Positionen sollen „anteilig in Statistik/Prognose“ einfließen, gleichzeitig sollen Statistikwerte mit dem Dashboard übereinstimmen und Einmalzahlungen exakt berücksichtigt werden. | Standard überall: **exakt im Fälligkeitsmonat** (Dashboard, Budget, Cashflow, Kontostand-, Jahresend-Prognose, Verlauf). Statistik bietet zusätzlich den Umschalter **„geglättet“**, der nicht-monatliche Positionen anteilig (Betrag ÷ Intervall) auf alle Monate verteilt. Sparziel-Prognosen glätten über den 3-Monats-Durchschnitt. |
| I-03 | F-031/F-070 vs. Datenmodell | Ein Startsaldo/Kontostand wird benötigt, das Datenmodell enthält keinen. | Neue Entität **`BalanceCheckpoint`** (Kontostand-Abgleich: Datum, Betrag). Ohne Abgleich startet die Rechnung bei 0 € mit Hinweis; die Cashflow-Ampel ist dann neutral. |
| I-04 | F-043 vs. F-071/F-072 | F-043 steht unter *Polster*, das Beispiel (Restschuld) betrifft Schulden; Überschneidung mit F-071/F-072. Zudem widerspricht „3-Monats-Durchschnitt“ der Forderung „aktualisiert sich bei Ratenänderung“. | Gemeinsame Prognose-Engine. Der 3-Monats-Durchschnitt umfasst die **zwei Vorperioden + die aktuelle Periode inkl. Plan**, dadurch wirkt eine Ratenänderung sofort. Für Schulden mit festem Zins/Rate hat die **Annuitätenrechnung (F-071)** Vorrang; die lineare Methode gilt für Schulden ohne feste Rate. |
| I-05 | F-073 | „Restausgaben > Resteinnahmen“ wäre nach Gehaltseingang fast immer wahr (Resteinnahmen = 0). | Vergleich: **Restausgaben > Kontostand heute + Resteinnahmen** (ohne Abgleich: bisherige Einnahmen − bisherige Ausgaben + Resteinnahmen). Zusätzlich Warnung bei Kategorie-Überschreitung (Ist > Soll). |
| I-06 | F-051 | Ampel „je Bereich“ gefordert, Schwellwerte nur für *Unverplant* definiert. | Konfigurierbare Regeln für alle Bereiche (§4.5), Standardwerte orientiert an 50/30/20 und BudgetHeld 1.3 (Kategorie-Ampeln, Spar-/Investquote). |
| I-07 | F-100 vs. F-003 | Währung im *Profil*, aber haushaltsweit; Mitglieder dürfen nur Budgetdaten bearbeiten. | Währung, Monatsstart-Tag, Haushaltsname und Ampel-Regeln sind **Haushaltseinstellungen – nur der Owner** darf sie ändern. Profil enthält Name, E-Mail, Sprache, Benachrichtigungen. |
| I-08 | F-101 | Starttage 29–31 sind mehrdeutig (Februar); Wechsel des Starttags erzeugt Lücken/Überlappungen. | Erlaubt sind **1–28**. Eine Periode endet am Tag *Start − 1* des namensgebenden Monats und beginnt am Tag nach dem Ende der Vorperiode → Übergangsperioden werden automatisch verlängert/verkürzt, nie lückenhaft. Änderungen gelten ab der nächsten Periode. |
| I-09 | F-111 vs. Haushalt | „Daten anderer Mitglieder bleiben unberührt“, Budgetdaten gehören aber dem Haushalt. | Kontolöschung entfernt **persönliche Daten** (Nutzer, Sitzungen, Passkeys, Benachrichtigungen, Push-Abos). Sind weitere Mitglieder vorhanden, bleibt der Haushalt bestehen und die Owner-Rolle geht an das am längsten beteiligte Mitglied; als einziges Mitglied wird der Haushalt mitgelöscht. Löschung erfolgt sofort; Backups altern nach 14 Tagen aus (< 30 Tage). |
| I-10 | F-001 vs. Original | Lastenheft: Magic Link; Original: E-Mail-Code + Passkey. Links öffnen in iOS-PWAs im Safari statt in der App. | Die Mail enthält **Link und 6-stelligen Code** (beide 15 Min., einmalig). Zusätzlich **Passkey-Login** (Face ID/Fingerabdruck) wie im Original 1.5. |
| I-11 | F-001 vs. F-140 | „Nach Klick direkt im Dashboard“ vs. geführte Ersteinrichtung. | Nutzer landet im Dashboard; bei neuem Haushalt öffnet sich der Assistent darüber (jederzeit überspringbar). |
| I-12 | F-062 | „Ist-Werte summieren zu 100 %“ – Bezugsgröße offen. | Basis = Bedarf + Wunsch + Sparen (+ ggf. „ohne Zuordnung“); Sparen umfasst Polster-Zuweisungen. Rundung per Largest-Remainder → Summe exakt 100,0 %. |
| I-13 | F-131 | „Kein Datenverlust“ bei gleichzeitiger Bearbeitung. | Feldweises 3-Wege-Merge. Bei echtem Konflikt (gleiches Feld) gewinnt die spätere Änderung, der überschriebene Wert wird im **Änderungsverlauf** gespeichert und ist wiederherstellbar; Betroffene werden benachrichtigt. |
| I-14 | Anhang A | Datenmodell unvollständig: `HouseholdId` fehlt bei mehreren Entitäten; kein Pausiert-Flag (F-011), keine Gültigkeit ab/bis Monat (F-010/F-101), kein Schuldentyp/Laufzeit/Abbuchungstag, keine Einladungen/Token. | Datenmodell erweitert (§5). |
| I-15 | F-010 vs. Historie | „Löschen entfernt Position aus allen Berechnungen“ zerstört Historie (z. B. gekündigtes Abo). | **Löschen** entfernt die Position überall (auch aus abgeschlossenen Monaten, Snapshots werden neu berechnet). Zusätzlich **„Beenden ab Monat“**, das die Historie erhält. |
| I-16 | F-042 | „Entnahme erhöht Unverplant/Ausgabe“ – zwei Bedeutungen. | Entnahme mit Option **„ins Budget übernehmen“** (erhöht Unverplant der Periode, Standard) oder **„direkt ausgegeben“** (reduziert nur den Polster-Stand). Grund ist Pflicht (Historie). |
| I-17 | F-012 | „Sonstiges“ wird vorausgesetzt, aber nicht definiert. | Systemkategorien *Sonstiges* (Ausgaben) und *Sonstige Einnahmen* sind nicht löschbar und Ziel beim Umhängen. |
| I-18 | F-061 | „Fehlende Monate als Lücke“ – wann fehlt ein Monat? | Perioden vor dem ersten Haushaltsmonat sowie Perioden ganz ohne Positionen sind Lücken (`null`). |
| I-19 | F-065 | „Ohne Vorjahr kein falscher Prozentwert“ – was gilt bei *teilweise* vorhandenem Vorjahr (z. B. Start im November)? | Der Vorjahresvergleich nutzt nur Monate mit Daten in **beiden** Jahren (like-for-like); die Seite nennt die verglichenen Monate. |

## 3. Zusätzlich übernommene Funktionen des Originals (BudgetHeld)

Recherche budgetheld.io inkl. Versionshinweise 1.0–2.0:

| ID | Funktion |
|----|----------|
| B-01 | 8 Budgetbereiche (als Unterkategorien gem. D-01) inkl. *Guilt Free*, *Geben*, *Investieren* |
| B-02 | Monatszentrale mit **Zielzone** (Unverplant → 0 €) |
| B-03 | Übernahme der Vormonatswerte, nur Änderungen bearbeiten („Delta-Editing“): Änderung *nur diesen Monat* oder *ab diesem Monat* |
| B-04 | **Rückgängig innerhalb 30 Sekunden** nach dem Löschen |
| B-05 | Leere Kategorien automatisch ausblenden + Auswahl zum Wiedereinblenden |
| B-06 | Kreditarten: Ratenkredit, Baufinanzierung/Hypothek (mit Zinsbindung), Leasing (Laufzeit, Restwert), Privatschuld, Kreditkarte, Dispo; **Tilgungsplan** (Zins/Tilgung je Monat), **Zusatztilgung**, Schuldenfrei-Datum |
| B-07 | Kredit-Positionen sind direkt im Budget verknüpft (Rate = Budgetposition) |
| B-08 | Cashflow-Kalender mit Abbuchungstagen, **Engpass-Vorhersage** (Monate mit Minus-Risiko) |
| B-09 | Ampeln je Kategorie + Spar-/Investquote |
| B-10 | **6 Finanzrechner** öffentlich ohne Login: Zinseszins, Kredit, Stundenlohn, Ruhestand, Hypothek, Tilgung (**Schneeball**) |
| B-11 | Finanz-Glossar (≥ 29 Begriffe) |
| B-12 | Sprachen **Deutsch/Englisch** (Umschalter) |
| B-13 | Währungen EUR, CHF, USD (+ weitere) |
| B-14 | Teilen-Funktion (Web Share API, WhatsApp, Facebook, X, LinkedIn; Instagram über System-Teilen) |
| B-15 | Passkey-Login (Face ID, Fingerabdruck, Gerätecode) |
| B-16 | Notgroschen-Vorschlag: Ziel = 3 × monatliches Nettoeinkommen |
| B-17 | Verschlüsselte Speicherung sensibler Finanzdaten |
| B-18 | Persistente Statusleiste (Monat + Überschuss) |
| B-19 | Versionsnummer im Footer, Versionshinweise-Seite |
| B-20 | Mobile Darstellung: eine Zeile je Position, Details aufklappbar |
| B-21 | Hinweise bei Synchronisationsfehlern |
| B-22 | Feedback-Funktion |

## 4. Fachliche Rechenregeln

### 4.1 Perioden (Budgetmonate, F-101)
* Monatsstart-Tag `d ∈ [1, 28]`. Periode *(J, M)* endet am Tag `d − 1` des Kalendermonats M (bei `d = 1` am Monatsletzten von M).
* Beginn = Ende der Vorperiode + 1 Tag. Beispiel `d = 25`: „November“ = 25.10.–24.11.
* Änderung des Starttags gilt ab der nächsten Periode (Historie in `MonthStartRules`).

### 4.2 Positionen, Rhythmus, Fälligkeit (F-010, F-011, F-013)
* Rhythmen: monatlich, alle 2 Monate, vierteljährlich, halbjährlich, jährlich, einmalig.
* Fällig in Periode P ⇔ `Start ≤ P ≤ Ende` und `(P − Start) mod Intervall = 0`; einmalig nur in `Start`.
* **Plan(P)** = Monatsabweichung(P) ?? letzte Betragsänderung mit `ab ≤ P` ?? Grundbetrag; 0 wenn pausiert.
* **Ist(P)** = erfasster Ist-Betrag; **effektiv(P)** = Ist ?? Plan.
* Datum in der Periode aus Abbuchungstag *x*: Kalendermonat = Monat des Periodenbeginns, falls `d > 1` und `x ≥ d`, sonst Monat M; Tag wird auf das Monatsende begrenzt (31. → 30./28.) und in die Periode geklemmt. Ohne Abbuchungstag: Periodenbeginn.

### 4.3 Budget-Kennzahlen (F-014, F-050)
* Einnahmen `E`, Ausgaben `A` (inkl. Kreditraten, Spar-/Investitionsausgaben), Polster-Zuweisungen `Z` (Zuweisungspositionen + manuelle Zuweisungen mit Budgetwirkung), Entnahmen ins Budget `W`, Übertrag aus Vormonat `Ü`.
* **Überschuss = E − A** (F-050)
* **Unverplant = Ü + E − A − Z + W** (F-014; Ü und W sind Erweiterungen, ohne sie gilt exakt E − A − Z)

### 4.4 Cashflow & Kontostand (F-030, F-031, F-070)
* Tagesgenaue Flüsse: Einnahmen (+), Ausgaben (−), Zuweisungen an Polster mit *separatem Konto* (−), Entnahmen ins Budget (+).
* Saldo am Tag t = letzter Kontostand-Abgleich ≤ t + Flüsse danach; liegt der erste Abgleich nach t, wird rückwärts gerechnet; ohne Abgleich Start bei 0 € (Hinweis).
* Saldo Periodenende = Start + Zuflüsse − Abflüsse; Tage mit Saldo < 0 sind **kritisch**.
* Vergangene Tage verwenden Ist-Werte, zukünftige Plan-Werte.

### 4.5 Ampel (F-051) – Standardregeln (konfigurierbar je Haushalt)
| Bereich | Grün | Gelb | Rot |
|--------|------|------|-----|
| Budget (Unverplant U) | U ≥ 0 | −10 % E ≤ U < 0 | U < −10 % E |
| Cashflow (min. Tagessaldo m) | m ≥ 10 % E | 0 ≤ m < 10 % E | m < 0 (ohne Abgleich: neutral) |
| Schulden (Raten ÷ E) | ≤ 20 % | ≤ 35 % | > 35 % oder Rate deckt Zinsen nicht |
| Polster (Stand ÷ monatl. Ausgaben) | ≥ 3 Monate | ≥ 1 Monat | < 1 Monat |
| Bedarf / Wunsch (Anteil an E) | ≤ 50 % / ≤ 30 % | ≤ 60 % / ≤ 40 % | darüber |
| Sparquote | ≥ 20 % | ≥ 10 % | < 10 % |
| Wohnen / Mobilität / Lebenshaltung / Guilt Free | ≤ 30/10/15/10 % | ≤ 40/15/25/15 % | darüber |
Jede Ampel zeigt zusätzlich **Symbol + Text** (✓ Gut, ! Achtung, ✕ Kritisch) und einen Tooltip mit Rechenweg.

### 4.6 Monatsabschluss (I-01)
* Voraussetzung: Periode hat begonnen. Ablauf: offene Ist-Werte prüfen → Rest-Unverplant: *in nächsten Monat übertragen* (`Ü`) oder *einem Polster zuweisen* → optional Kontostand am Periodenende → Snapshot.
* Snapshot speichert Summen + alle Positionszeilen (Plan, Ist, Kategorie, Gruppe). Abgeschlossene Perioden sind schreibgeschützt; Wiedereröffnen löscht den Snapshot.

### 4.7 Schulden (F-020–F-022, F-071)
* Restschuld = Anfangs-Restschuld − Σ Tilgungsanteile (chronologisch).
* Reguläre Rate: Zinsanteil = Rest × Zinssatz ÷ 12 (Zinssatz zum Buchungszeitpunkt), Tilgung = Rate − Zins; ohne Zins: Tilgung = Rate. Sondertilgung: komplett Tilgung. Tilgung wird auf die Restschuld begrenzt; bei Rest 0 → „getilgt“.
* Fortschritt = (Ursprungssumme − Rest) ÷ Ursprungssumme.
* Verknüpfte Budgetpositionen (Rate, Zusatztilgung) werden am Fälligkeitstag automatisch als Zahlung verbucht (abschaltbar), nur für Fälligkeiten **nach** dem Erfassungsdatum der Restschuld.
* Schuldenfrei-Prognose: monatliche Simulation (Rate + geplante Zusatztilgungen), geschlossen: `n = −ln(1 − r·B/A) / ln(1 + r)`; ohne Zins `n = ⌈B/A⌉`. Gesamt = späteste Einzelprognose; zusätzlich Schneeball-/Lawinen-Variante.

### 4.8 Polster (F-040–F-043, F-072)
* Stand = Σ Zuweisungen − Σ Entnahmen (inkl. fälliger, noch nicht gebuchter Sparplan-Raten). Fortschritt = Stand ÷ Ziel × 100.
* Sparplan = Budgetposition vom Typ *Zuweisung*, wird am Fälligkeitstag als Transaktion gebucht.
* Entnahme ≤ aktueller Stand, Grund Pflicht.
* Prognose: Ø Nettozuweisung (Zuweisungen − Entnahmen) der letzten 3 Perioden inkl. aktueller → Monate = ⌈(Ziel − Stand) ÷ Ø⌉; Ø ≤ 0 → „Keine Prognose möglich“.

### 4.9 Statistik (F-060–F-068)
* Sparquote = (Ausgaben der Gruppe Sparen + Z − W) ÷ E × 100; 3-Monats-Durchschnitt (Mittel der Quoten) ab dem 3. Monat.
* Top-Ausgaben: Anteil = Betrag ÷ Gesamtausgaben × 100.
* Jahresvergleich: Abweichung = (Jahr − Vorjahr) ÷ |Vorjahr| × 100; ohne Vorjahr „–“; |Abw.| > 20 % hervorgehoben.
* Soll/Ist: Abweichung = Ist − Soll; Ausgaben-Überschreitung rot, Punktlandung grün; Σ Abweichungen = Gesamtabweichung.
* Fixkostenquote = fixe Ausgaben ÷ E × 100; > 60 % Warnung.
* Vermögen = Σ Polster-Stand − Σ Restschulden (zum Periodenende).

### 4.10 Prognosen (F-070–F-075)
* Kontostand: Saldo(P+1) = Saldo(P) + E − A − Z(separates Konto) + W.
* Jahresend-Prognose: Ø der regelmäßigen (monatlichen) Beträge der bisherigen Perioden × 12 + nicht-monatliche/einmalige Positionen exakt.
* Szenario: Änderungen wirken auf einer Kopie der Daten; „Verwerfen“ verwirft die Kopie, „Übernehmen“ (Erweiterung) schreibt sie nach Bestätigung zurück.

## 5. Datenmodell-Erweiterungen (gegenüber Anhang A)

| Entität | Ergänzung |
|---------|-----------|
| alle Haushaltsdaten | `HouseholdId`, `Version`, `UpdatedAt/By`, `IsDeleted` (Sync-Tombstone), `IsDemo` |
| Household | `MonthStartRules` (Historie), `FirstPeriod`, `AmpelRules`, `ChangeSeq` |
| Category | `Group` (Bedarf/Wunsch/Sparen), `SortIndex`, `IsSystem` |
| BudgetEntry | `Kind` (Einnahme/Ausgabe/Zuweisung), `StartPeriod`, `EndPeriod`, `Note`, `DebtId` + `DebtRole`, `BufferId` |
| BudgetEntryChange (neu) | Betragsänderung/Pause „ab Monat“ |
| BudgetEntryMonth (neu) | Monatsabweichung „nur dieser Monat“, Ist-Betrag, abgehakt |
| DebtEntry | `Name`, `Type`, `OpeningBalance`, `StartDate`, `TermMonths`, `DebitDay`, `FixedInterestUntil`, `ResidualValue`, `AutoBook` |
| DebtPayment | `IsExtra`, `InterestRateApplied`, `SourceEntryId/-Period` |
| BufferEntry | `SeparateAccount`, `IsEmergencyFund`, `TargetDate`, `SortIndex` |
| BufferTransaction | `AffectsBudget`, `SourceEntryId/-Period` |
| MonthSnapshot | `HouseholdId`, `TotalAllocations`, `CarryOver`, `ClosingBalance`, `Lines` |
| BalanceCheckpoint (neu) | Kontostand-Abgleich |
| Invitation, LoginToken, RefreshToken, PasskeyCredential, PushSubscription, Notification, NotificationLog, ChangeLog, ApiToken (neu, technisch) | |
| User | `BlockedAt`, `BlockedReason` (Sperre durch Administrator, V-04) |
| ServerSetting (neu, technisch) | Laufzeit-Einstellungen des Servers, z. B. Registrierungsmodus und Freigabeliste (V-02) |

## 6. Betrieb und Verwaltung (Erweiterung, 2026-10-03)

Nicht im Lastenheft gefordert; ergänzt für den Betrieb auf einem eigenen Raspberry Pi mit ~10 Nutzern.

| ID | Thema | Entscheidung |
|----|-------|--------------|
| V-01 | Wer ist Administrator? | Festgelegt über die Konfiguration `App:AdminEmails` statt über eine Rolle in der Datenbank: kein Henne-Ei-Problem bei der Einrichtung, keine Rechteausweitung über die App möglich. Administratoren lassen sich in der App weder sperren noch löschen (erst aus der Konfiguration entfernen). |
| V-02 | Registrierung | Wunsch des Auftraggebers: jeder darf sich registrieren → Standard **Offen**. Zusätzlich zur Laufzeit umschaltbar: **Auf Anfrage** (V-06), **Nur Freigegebene** (Adressen oder ganze Domains, Personen mit E-Mail-Einladung, Anmeldung über einen gültigen Einladungslink) und **Geschlossen** (nur Administratoren). Bestehende Konten sind nie betroffen. Voreinstellung per `App:DefaultRegistrationMode`; auf dem Pi (öffentliche Funnel-Adresse) *Auf Anfrage*. |
| V-03 | Reihenfolge der Prüfungen beim Anmelden | Sperre und Registrierungsmodus werden **vor** dem Mail-Rate-Limit geprüft: für abgelehnte Adressen wird keine Mail verschickt (Schutz des SMTP-Postfachs), die Meldung ist eindeutig. In Kauf genommen: Bei eingeschränkter Registrierung ist erkennbar, ob zu einer Adresse ein Konto existiert – für einen kleinen, privaten Nutzerkreis vertretbar. |
| V-04 | Sperren und Löschen | Sperren beendet sofort alle Sitzungen, API-Zugänge und Push-Abos und meldet offene Geräte über SignalR ab; die Daten bleiben erhalten, Entsperren jederzeit. Löschen verhält sich wie F-111 (Owner-Rolle geht an ein anderes Mitglied, gemeinsame Daten bleiben). Alle Verwaltungsaktionen werden protokolliert. |
| V-06 | Registrierung „Auf Anfrage“ | Nachfrage des Auftraggebers (2026-10-03): jeder soll sich selbst registrieren können, ohne dass Fremde Mails an beliebige Adressen auslösen. Neue Adressen erzeugen eine Anfrage **ohne Mail**; Administratoren erhalten höchstens alle 30 Minuten einen Hinweis per Mail; Freischalten setzt die Adresse auf die Freigabeliste und schickt erst dann den Anmeldelink. Ablehnen verschickt nichts. Schutz vor Fluten: eine Anfrage je Adresse, max. 200 offene, Verfall nach 30 Tagen, IP-Rate-Limit. |
| V-07 | Keine Registrierung mit Passwort | Eine klassische Registrierung mit Passwort löst das Mail-Problem nicht (die Adresse muss trotzdem per Mail bestätigt werden, sonst könnten Fremde Adressen „besetzen“) und widerspräche F-001 (Anmeldung ohne Passwort). Stattdessen: V-06 und Passkeys (B-15) für die Anmeldung ohne Mail. |
| V-05 | Hosting | Raspberry Pi zu Hause statt IONOS-Webspace (Shared Webspace erlaubt keine dauerhaft laufenden .NET-Prozesse/WebSockets). Öffentliche HTTPS-Adresse über **Tailscale Funnel**: gültiges Zertifikat, keine Portfreigabe, keine eigene Domain. Die App lauscht nur auf 127.0.0.1; systemd-Härtung; Updates mit Datenbanksicherung und automatischem Rollback (`deploy/pi`). |
