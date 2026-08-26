# Lottery Lab

MVP completo para importar resultados de PDFs, persistir o histórico, calcular ranking estatístico, executar backtest e opcionalmente pedir uma análise à OpenAI.

> Importante: frequência, atraso e reversão são estatísticas descritivas. Em sorteios independentes, resultados anteriores não tornam uma combinação futura matematicamente mais provável.

## Stack
- Angular 19
- .NET 8 Web API + Dapper
- PostgreSQL
- PdfPig para PDFs com texto selecionável
- OpenAI Responses API opcional

## Rodar localmente
Pré-requisitos: Docker Desktop.

```bash
# opcional, para análise por IA
# Linux/macOS
export OPENAI_API_KEY="sua-chave"
# PowerShell
$env:OPENAI_API_KEY="sua-chave"

docker compose up --build
```

Acesse:
- Frontend: http://localhost:4200
- Swagger: http://localhost:8080/swagger
- Health: http://localhost:8080/health

## Fluxo
1. Selecione um PDF.
2. A API extrai texto e tenta detectar data, banca, horário, posições, números e grupos.
3. Confira a prévia e confirme.
4. Selecione banca/horário/janela.
5. Rode Forecast e Backtest.
6. Opcional: use "Analisar com IA".

## PDFs escaneados
A versão inicial usa PdfPig e funciona melhor quando o PDF contém texto real. PDF que é apenas uma imagem precisa de OCR. A arquitetura deixa o `PdfImportService` isolado para acrescentarmos OCR depois.

## Banco
Execute `database/schema.sql` no PostgreSQL de produção.

## Hospedagem recomendada
### Banco: Neon
1. Crie um projeto gratuito no Neon.
2. Copie a connection string PostgreSQL.
3. Abra o SQL Editor e execute `database/schema.sql`.

### Backend: Render
1. Suba este projeto para o GitHub.
2. Render > New > Web Service > conecte o repositório.
3. Root Directory: `backend/LotteryLab.Api`
4. Runtime: Docker.
5. Variáveis:
   - `ConnectionStrings__Default` = connection string do Neon
   - `OPENAI_API_KEY` = opcional
6. Faça deploy e copie a URL, por exemplo `https://seu-backend.onrender.com`.

No plano gratuito do Render, a API pode entrar em suspensão após inatividade. É adequado para MVP/testes, não para produção crítica.

### Frontend: Cloudflare Pages
Antes do deploy, altere em `frontend/src/app/app.component.ts` o fallback de API de produção. Para simplificar, substitua:

```ts
api=(window as any).__API_URL__ || 'http://localhost:8080/api';
```

por:

```ts
api = location.hostname === 'localhost'
  ? 'http://localhost:8080/api'
  : 'https://SEU-BACKEND.onrender.com/api';
```

Depois:
1. Cloudflare > Workers & Pages > Create > Pages > Git.
2. Root Directory: `frontend`
3. Build command: `npm run build`
4. Build output: `dist/lottery-lab-web/browser`

## OpenAI
A API usa `POST /v1/responses`. A chave fica **somente no backend** via `OPENAI_API_KEY`; nunca coloque a chave no Angular.

A IA recebe resultados agregados do Forecast/Backtest. O banco continua sendo a fonte da verdade, portanto o histórico não depende de uma conversa específica do ChatGPT.

## Endpoints
- `POST /api/imports/preview` — multipart PDF
- `POST /api/imports/commit` — confirma a prévia
- `GET /api/history`
- `GET /api/forecast?bank=LT%20NACIONAL&time=21:00&windowDays=15&top=10`
- `GET /api/backtest?...`
- `POST /api/ai/analyze`
- `GET /health`

## Próximas evoluções recomendadas
- OCR para PDFs escaneados.
- Cadastro de bancas e horários.
- Tela de edição da prévia antes de confirmar.
- Importação em lote.
- Backtest separado para Continuidade / Atraso / Reversão / Híbrido, com versões de algoritmo.
- Registro imutável de previsões antes do resultado.
- Comparação por grupo, dezena, centena e milhar.
- Otimização de pesos somente em treino e validação fora da amostra.
- Login.
- Backup/exportação CSV/Excel.
