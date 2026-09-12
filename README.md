# GarageFlow Serverless

Funções de autenticação de clientes por CPF e autorização do API Gateway para o Tech Challenge Fase 3. A aplicação GarageFlow mantém as regras de negócio e a verificação de credenciais; a função de autenticação valida a entrada, consulta a API privada e emite o JWT do cliente.

Este repositório tem ciclo de build e deploy próprio. Metadados de infraestrutura são consumidos por contratos versionados; não há referência de projeto para outro checkout.

## Runtime .NET

O runtime usa .NET 10 (`net10.0`) no modelo de deployment por arquivo ZIP da runtime gerenciada `dotnet10`. O projeto `Host` é uma class library e contém somente tradução dos eventos AWS e composição das dependências. As regras e portas seguem para dentro pelas camadas `Domain`, `Application` e `Adapters.Infrastructure`; não há persistência nem referência à solução principal GarageFlow.

Os handlers publicados são:

- autenticação: `GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.CustomerAuthenticationFunction::FunctionHandler`
- authorizer HTTP API REQUEST 2.0: `GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.RequestAuthorizerFunction::FunctionHandler`

A função de autenticação atende `POST /auth/customers/token` no formato proxy HTTP API 2.0. O corpo JSON contém `cpf` e `password`; o CPF é validado e normalizado para 11 dígitos antes da chamada privada. A resposta de sucesso contém `token`, `mustChangePassword`, `tokenType` e `expiresIn`, com `Cache-Control: no-store`. O authorizer recebe um evento REQUEST 2.0 e devolve a resposta simples `isAuthorized`.

## Configuração do runtime

Somente ARNs dos segredos entram nas variáveis da Lambda. Os valores são buscados no AWS Secrets Manager pela cadeia normal de credenciais do runtime e permanecem em cache por no máximo 60 segundos.

| Variável | Função | Uso |
| --- | --- | --- |
| `JWT_SECRET_ARN` | ambas | ARN da chave HS256 dos JWTs de usuário |
| `INTERNAL_AUTH_SECRET_ARN` | autenticação | ARN da chave HS256 do token de serviço interno |
| `INTERNAL_API_BASE_URL` | autenticação | URL base fixa da API privada |
| `INTERNAL_API_TRANSPORT` | autenticação | `http` ou `https`, igual ao esquema da URL |
| `JWT_ISSUER` | ambas | emissor do JWT; padrão `GarageFlow` |
| `JWT_AUDIENCE` | ambas | audiência do JWT; padrão `GarageFlow.Adapters.Api` |

As chaves devem ter pelo menos 32 bytes UTF-8, não podem ser placeholders e devem ser diferentes. O cliente HTTP usa timeout de 5 segundos, não segue redirects e não repete solicitações. Erros de configuração, Secrets Manager, transporte ou identidade inválida falham fechados sem incluir credenciais, tokens ou respostas privadas na resposta HTTP.

## Build, testes e pacote

```powershell
dotnet build GarageFlow.Serverless.slnx --configuration Release
dotnet test Tests/Unit/GarageFlow.Serverless.Tests.Unit.csproj --configuration Release
dotnet test Tests/Integration/GarageFlow.Serverless.Tests.Integration.csproj --configuration Release
dotnet test GarageFlow.Serverless.slnx --configuration Release --settings coverlet.runsettings --collect:"XPlat Code Coverage"
dotnet publish Host/GarageFlow.Serverless.Host.csproj --configuration Release --runtime linux-x64 --self-contained false --output <diretorio-externo>
```

Testes de integração usam handlers HTTP controlados, tokens JWT reais e mocks do SDK do Secrets Manager. Eles não exigem credenciais AWS nem acesso à rede.
