using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Web.Models;

public static class AnalysisMessages
{
    public static string Error(AnalysisErrorCode code) => code switch
    {
        AnalysisErrorCode.MissingConfiguration => "Gemini API anahtarı veya model adı yapılandırılmamış.",
        AnalysisErrorCode.Authentication => "Gemini API anahtarı geçersiz veya süresi dolmuş. Anahtarı kontrol edin.",
        AnalysisErrorCode.PermissionDenied => "Gemini projesinin erişim, bölge veya faturalandırma izinlerini kontrol edin.",
        AnalysisErrorCode.ModelUnavailable => "Seçili Gemini modeli bu hesap için kullanılamıyor.",
        AnalysisErrorCode.QuotaExceeded => "Gemini projesinin istek/token kotasına ulaşıldı. Google AI Studio kota ve faturalandırma ekranını kontrol edin.",
        AnalysisErrorCode.ServiceUnavailable => "Gemini servisi geçici olarak yoğun veya kullanılamıyor.",
        AnalysisErrorCode.Timeout => "Gemini isteği zaman aşımına uğradı.",
        AnalysisErrorCode.NetworkError => "Gemini servisine bağlantı kurulamadı.",
        AnalysisErrorCode.Blocked => "Gemini güvenlik filtresi nedeniyle yanıt üretmedi. Talebi elle gözden geçirin.",
        AnalysisErrorCode.Truncated => "Gemini yanıtı tamamlanmadan kesildi.",
        AnalysisErrorCode.InvalidResponse => "Gemini yanıtı beklenen veri biçiminde değildi.",
        _ => ""
    };
}
