# SmartSupportCRM

SmartSupportCRM, müşteri destek taleplerini SQL Server'da saklayan ve Gemini ile özet, yanıt taslağı, duygu, öncelik ve kategori üreten bir ASP.NET Core MVC uygulamasıdır.

## Mimari

- **Domain** — Talep modeli, analiz durumları ve hata kodları.
- **Application** — Talep işlemleri ve model çağrısı için servis sözleşmeleri.
- **Infrastructure** — EF Core, SQL Server migration'ları, analiz kuyruğu ve Gemini istemcisi.
- **Web** — Razor ekranları, MVC controller'ları, sağlık uçları ve analiz worker'ı.
- **Tests** — xUnit testleri, geçici SQLite veritabanı ve sentetik ONNX dosyaları.

Yeni talep önce SQL'e kaydedilir. Arka plan worker'ı kaydı kuyruktan alır, Gemini'ye başlık ve içerik gönderir ve doğrulanmış sonucu aynı kayda yazar. Geçici hatalarda kontrollü yeniden deneme uygulanır. Önerilen yanıt temsilci incelemesi için bir taslaktır.

## Yerelde çalıştırma

Gereksinimler Windows, .NET 9 SDK ve SQL Server Express LocalDB'dir. Varsayılan bağlantı mevcut Windows kullanıcısının MSSQLLocalDB örneğini kullanır.

~~~powershell
dotnet restore SmartSupportCRM.sln
~~~

EF CLI aracı yoksa 9.x sürümünü yükleyin:

~~~powershell
dotnet tool install --global dotnet-ef --version 9.0.8
~~~

Veritabanı şemasını uygulayın:

~~~powershell
dotnet ef database update --project SmartSupportCRM.Infrastructure --startup-project SmartSupportCRM.Web
~~~

Gemini anahtarını aynı PowerShell oturumunda ortam değişkeni olarak verin. PowerShell 7'de aşağıdaki komut anahtar girişini ekranda gizler:

~~~powershell
$env:Gemini__ApiKey = Read-Host 'Gemini API anahtarı' -MaskInput
dotnet run --project SmartSupportCRM.Web --launch-profile http
~~~

Uygulama [localhost:5071](http://localhost:5071) adresinde açılır. Windows'ta bağlantı ön kontrolü ve başlatma için sırasıyla **.\Start-CRM.ps1 -CheckOnly** ve **.\Start-CRM.ps1** komutları da kullanılabilir. Model adlarını gerekirse **Gemini__Model** ve **Gemini__FallbackModel** ortam değişkenleriyle değiştirin.

## Testler

~~~powershell
dotnet test SmartSupportCRM.sln
~~~

Otomatik testler talep kuyruğunu, model hata senaryolarını ve MVC davranışını kapsar. Testlerdeki sabit çıktılı ONNX grafikleri entegrasyon testi içindir. Uygulamanın aktif analiz akışı Gemini kullanır.

## Kullanım kapsamı

Bu proje yerel portföy demosu olarak hazırlanmıştır. Talep görüntüleme ve toplu silme eylemlerinde kullanıcı girişi ve rol denetimi henüz yoktur. Bir sunucuya dağıtmadan önce erişim denetimlerini ekleyin.
