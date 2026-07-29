namespace BackendApi.Contracts;

// Curriculum "regulation" (e.g. R20-style admission-batch curriculum scheme). A Subject's
// own identity (code/name/department/coordinator) stays stable across regulations - the
// per-regulation curriculum detail lives in RegulationSubjectOfferingDto below. Schema
// pattern synthesized from public regulation-document structure, not verified against a
// real proprietary SIS's source - see the PR description.
public record RegulationDto(
    Guid Id, Guid DepartmentId, string Code, string Name, int EffectiveFromYear, bool IsActive);

public record CreateRegulationRequest(Guid DepartmentId, string Code, string Name, int EffectiveFromYear);

// Code and DepartmentId are the regulation's identity and aren't editable here - only
// name/active-status can change after creation.
public record UpdateRegulationRequest(string Name, bool IsActive);

// L-T-P-C (Lecture-Tutorial-Practical-Credits) - accreditation-mandatory per NBA/NAAC
// manuals, the highest-priority field addition identified by the Subjects redesign research.
public record RegulationSubjectOfferingDto(
    Guid Id,
    Guid RegulationId,
    Guid SubjectId,
    string SubjectCode,
    string SubjectName,
    int Semester,
    int LectureHours,
    int TutorialHours,
    int PracticalHours,
    decimal Credits,
    bool IsElective,
    bool IsLab,
    decimal MinAttendancePercent);

public record CreateOfferingRequest(
    Guid SubjectId,
    int Semester,
    int LectureHours,
    int TutorialHours,
    int PracticalHours,
    decimal Credits,
    bool IsElective,
    bool IsLab,
    decimal MinAttendancePercent);

public record UpdateOfferingRequest(
    int Semester,
    int LectureHours,
    int TutorialHours,
    int PracticalHours,
    decimal Credits,
    bool IsElective,
    bool IsLab,
    decimal MinAttendancePercent);

// Syllabus structure under a per-regulation offering - keyed here rather than off Subject
// directly since unit/chapter breakdown is exactly the curriculum detail that changes
// between regulations. Also the target shape for the (not-yet-built) LLM-based AIS-06
// syllabus extraction pipeline.
public record CurriculumUnitDto(Guid Id, Guid OfferingId, int UnitNumber, string Title, string? Description);

public record CreateCurriculumUnitRequest(int UnitNumber, string Title, string? Description);

public record UpdateCurriculumUnitRequest(int UnitNumber, string Title, string? Description);

public record CurriculumChapterDto(Guid Id, Guid UnitId, int ChapterNumber, string Title, string? Description);

public record CreateCurriculumChapterRequest(int ChapterNumber, string Title, string? Description);

public record UpdateCurriculumChapterRequest(int ChapterNumber, string Title, string? Description);
