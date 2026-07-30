namespace BackendApi.Data.Entities;

public enum AccountType { Student, Teacher, AdminTier, Parent }

public enum ScopeKind { Global, Department }

public enum AttendanceStatus { Present, Absent, Late }

public enum AssignmentType { Code, Quiz, Essay, FileUpload }

public enum GroupType { Class, SubjectSection, Club, TeacherOnly }

public enum DocType { Pdf, Pptx, Docx }

public enum NotificationType
{
    ExitPing,
    AbsencePing,
    Report,
    TimetableRequest,
    FeeReminder,
    WhitelistRequest,
    SuspiciousFlag,
}

public enum FeeStatus { Pending, Paid }

public enum OcrStatus { Pending, Processing, Completed, Failed, NotApplicable }

public enum WhitelistRequestStatus { Pending, Approved, Rejected }

public enum TimetableChangeRequestStatus { Pending, Approved, Rejected }

public enum PaymentTransactionStatus { Confirmed, Failed }
