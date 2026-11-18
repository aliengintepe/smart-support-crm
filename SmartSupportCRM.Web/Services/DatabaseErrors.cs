using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace SmartSupportCRM.Web.Services;

public static class DatabaseErrors
{
    public static DbException? Find(Exception error)
    {
        if (error is DbException db) return db;
        if (error is AggregateException many)
            foreach (var inner in many.InnerExceptions)
                if (Find(inner) is DbException result) return result;
        return error.InnerException == null ? null : Find(error.InnerException);
    }
    public static string Message(DbException error) => error is SqlException { Number: -1983577846 }
        ? "SQL Server LocalDB bağlantısı kurulamadı. Aynı Windows kullanıcısının normal terminalinde Start-CRM.ps1 -CheckOnly ile bağlantıyı kontrol edin."
        : "Veritabanına erişilemiyor. SQL Server bağlantısını kontrol edin; kayıt başarıyla tamamlanmış gibi gösterilmeyecek.";
    public static int Code(DbException error) => error is SqlException sql ? sql.Number : error.ErrorCode;
}
