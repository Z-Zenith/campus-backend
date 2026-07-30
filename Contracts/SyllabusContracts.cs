namespace BackendApi.Contracts;

// AIS-06: one chapter under a unit, as returned by campus-ai-services' extractor.
public record SyllabusChapterExtractionDto(int ChapterNumber, string Title, string? Description);

// AIS-06: one unit of the syllabus, with its chapter breakdown.
public record SyllabusUnitExtractionDto(
    int UnitNumber, string Title, string? Description, IReadOnlyList<SyllabusChapterExtractionDto> Chapters);

// AIS-06: extracted fields returned for the caller (Admin/Teacher) to review. This is
// extraction only - "confirm and save" (persisting a reviewed extraction into
// CurriculumUnit/CurriculumChapter under a RegulationSubjectOffering) is a separate step:
// RegulationsController's CreateUnitsFromExtraction endpoint, which the caller passes its own
// (possibly admin-edited) copy of this response's Units to - this DTO is not itself accepted
// back as a request body anywhere.
public record SyllabusExtractionResponseDto(
    string? CourseCode,
    string? CourseName,
    double? Credits,
    IReadOnlyList<string> Textbooks,
    IReadOnlyList<SyllabusUnitExtractionDto> Units,
    IReadOnlyList<string> ConfidenceNotes);
