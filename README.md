# MOZ — Inteligência logística integrada
ASP.NET Core 8 + SQL Server · MonsterASP · GitHub Actions.
[Escopo completo e critérios de aceite](docs/ESCOPO.md).
**Estado:** implementação inicial, ainda não certificada para produção comercial. Não declarar os 37 módulos concluídos. Nenhuma credencial no repositório.
Configuração: ConnectionStrings__DefaultConnection, Jwt__Key, Jwt__Issuer, Jwt__Audience, App__BaseUrl, SeedAdmin__Name/Email/Password, Storage__RootPath/MaxFileSizeMB/AllowedExtensions. Os serviços externos e a camada nativa requerem implementação/validação adicional. A primeira inicialização cria as tabelas em banco vazio e a conta global; nunca usar EnsureCreated para evoluir schema existente: adotar migrações controladas antes da próxima alteração.
