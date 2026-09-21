# Motor Estatístico de Milhares · versão 1

## Integração com Gerar ranking

O botão existente **Gerar ranking** e a geração para o dia inteiro agora usam `MilharPredictionSelection`, que chama o modelo C deste motor. O contrato `/api/predictions/generate` continua o mesmo e aceita opcionalmente `statisticalConfiguration`. O histórico é limitado pela janela de dias do formulário e pelas posições do 1º ao prêmio selecionado. Sem restrição de animais, retorna exatamente as maiores pontuações. Com animais selecionados, mantém cotas equilibradas e seleciona os maiores scores dentro delas. Não aplica exploração aleatória ou penalidade baseada em previsões anteriores.

As previsões são identificadas por `MILHAR_STATISTICS V1`. Configuração e versão ficam no JSON da previsão; os sete scores em `features.statistics` e o ranking original em `features.statisticalRank`. A tela existente mantém escala de score 0–100; componentes internos ficam em 0–1. A seleção e os valores financeiros continuam gravados/conferidos nas mesmas tabelas. Previsões antigas preservam algoritmo e features originais.

A janela automática diária também compara as janelas usando o novo modelo, o prêmio e os animais escolhidos. Os presets automáticos por horário continuam sendo os valores existentes (não foram recalibrados para o novo motor). A bateria explicitamente rotulada V2/V3 continua disponível como comparação histórica dos modelos anteriores.

## Análise da arquitetura e reaproveitamento

O projeto usa .NET 8, controllers MVC, serviços com Dapper/Npgsql e PostgreSQL. Não existe camada de repositories: as consultas ficam nos serviços e usam `Db`. O frontend usa Angular 19 standalone, com navegação central em `AppComponent`, autenticação por interceptor e estilos globais. O novo componente segue essa navegação e esses estilos.

| Informação | Fonte existente |
|---|---|
| Loteria/banca | `extractions.bank` |
| Data | `extractions.extraction_date` |
| Horário | `extractions.extraction_time` |
| Prêmio | `results.position` |
| Milhar | `results.number`, texto de quatro dígitos |
| Centena/dezena | `results.centena` / `results.dezena`; o motor deriva os sufixos de `number` para manter consistência |
| Grupo/animal | `results.group_no` / `results.animal` |
| Relação | `results.extraction_id → extractions.id` |

`ParsedResult`/`ParsedExtraction` representam a importação; o histórico é consultado pelo `ApiController`. `AnalysisService` já oferece frequência/atraso/reversão e backtest de dezenas. `NumberGeneratorService` oferece ranking de sufixos com diversidade. `PredictionService` oferece previsões persistidas, seleção com exploração, backtests e bateria V2/V3. Essas seleções não representam o ranking integral determinístico pedido; foram preservadas e não são usadas como features deste motor.

Arquivos novos:

- `Models/MilharStatisticsModels.cs`: configuração, contratos e respostas.
- `Services/MilharStatisticsEngine.cs`: features, normalização e ranking puro.
- `Services/MilharBacktestEngine.cs`: avaliação, métricas, comparação, ablações e seleção na validação.
- `Services/MilharStatisticsService.cs`: leitura única do histórico existente e reconstrução de auditoria.
- `Controllers/MilharStatisticsController.cs`: endpoints com `analysis.use`.
- `LotteryLab.Api.Tests/MilharStatisticsTests.cs`: testes determinísticos do motor/backtest.
- `frontend/src/app/milhar-statistics.component.{ts,html}`: interface independente dentro de Análises.
- `frontend/tests/milhar-statistics-state.cjs`: configuração congelada, busca, persistência e auditoria da interface.

Arquivos existentes alterados: registro em `Program.cs`, importação/inclusão do componente em `app.component.ts/html`, README e expectativa antiga do teste de PDF. O teste de PDF esperava três dígitos, embora importador e gravação já normalizem todos os números para quatro. Nenhuma lógica de importação foi alterada. Não há tabela nova ou migração.

## Definições estatísticas

A configuração declara pesos, janelas, pesos das janelas, half-life, alpha e prior do horário. Pesos devem somar 1. A/B e ablações zeram componentes e renormalizam os pesos restantes; configurações sem peso ativo são rejeitadas.

1. **Dezena:** frequências relativas nas últimas 10/30/50/100 observações e em todo o passado. Quando faltam registros, o denominador é a quantidade efetivamente disponível. Pesos iniciais: 0,30/0,25/0,20/0,15/0,10.
2. **Centena:** mesmas janelas, com `(contagem + alpha)/(n + 1000*alpha)`.
3. **Dígitos:** frequências por posição A/B/C/D, suavizadas em cada janela; média dos quatro valores da candidata.
4. **Recência:** frequência da dezena ponderada por `exp(-ln(2)*distância/halfLife)`, dividida pela soma dos pesos. A distância conta observações; o último resultado tem distância zero.
5. **Horário:** média dos sinais de dezena, centena e dígitos calculados somente na banca/horário alvo. O global usa outros horários da mesma banca, respeitando o prêmio selecionado.
6. **Pares:** seis tabelas distintas AB/AC/AD/BC/BD/CD, com frequências suavizadas e ponderadas pelas janelas. O score é a média das seis frequências posicionais.
7. **Transição:** sequências separadas por prêmio, no horário alvo. Para o último resultado de cada sequência, estima dezena→dezena, centena→dezena, quatro transições posicionais e grupo→grupo quando disponível. Usa Laplace e média das sete probabilidades condicionais. Não liga prêmios simultâneos. Grupos candidatos seguem a convenção existente 01–04→1, …, 97–00→25; grupo ausente produz distribuição uniforme.

Cada componente recebe min/max nas 10.000 candidatas usando apenas as features históricas daquele corte. Componentes constantes ficam em zero. Depois aplica fatores de evidência à centena (`n/(n+1000*alpha)`), horário (`nHorario/(nHorario+prior)`) e transição (`suporte/(suporte+100*alpha)`). Aplicar esses fatores **depois** da normalização evita que min/max desfaça a proteção de amostras pequenas. Suporte de transição soma ocorrências condicionais das sete famílias, não é uma contagem de extrações independentes.

O score final combina os sete componentes com os pesos configurados. `scoreGlobal` exclui o componente horário e redistribui os pesos restantes. Nenhum score é uma probabilidade calibrada. Empates são resolvidos pela milhar crescente; não há seleção aleatória nem limite de diversidade no ranking.

## Walk-forward e escolha de configurações

O corte é estritamente `timestamp < alvo`. Todos os prêmios da extração alvo ficam fora, mesmo quando todos os prêmios são selecionados. Extrações anteriores do mesmo dia podem participar. Datas são locais, sem `Z`/offset; não há conversão implícita de fuso.

Os alvos elegíveis são filtrados pelo período inclusivo, banca, horário, prêmio e histórico mínimo. O histórico pode preceder o início do período. Os timestamps elegíveis são divididos em 60% treino, 20% validação e o restante teste, sem dividir prêmios simultâneos entre partições. O histórico cresce durante cada partição, como em uso real; os pesos permanecem fixos. A fase treino fornece desenvolvimento e observações iniciais, sem ajuste automático escondido.

Modelos:

- A: dezena, centena e recência.
- B: horário, pares e transição.
- C: todos os componentes.
- Ablações: C sem cada um dos sete componentes, incluindo dígitos.

Os modelos compartilham alvos e features para a mesma configuração. A busca opcional aceita até 12 configurações completas, mede MRR apenas na validação, escolhe o maior (empates ficam com a primeira configuração) e só depois avalia o vencedor no teste. O teste do vencedor é executado uma vez por chamada. Consultar o teste repetidamente para escolher configurações manualmente compromete a avaliação; a API não impõe um bloqueio permanente entre chamadas.

Na interface, a configuração vencedora e os filtros ficam em `localStorage` deste navegador, sob `lottery-lab-milhar-validation`. O relatório JSON exportado contém configurações, busca de validação, métricas e testes individuais. Não existe persistência de experimentos no servidor nesta versão; é preciso exportar o relatório para arquivamento durável e compartilhamento.

## Métricas e baseline

Cada prêmio é um teste individual. Top K da centena/dezena verifica se o sufixo aparece entre as **K milhares** mais bem classificadas, e não entre K sufixos distintos. As taxas usam escala 0..1. MRR é média de `1/rank`; ranking médio, mediano e as nove faixas são calculados sobre todos os testes da partição. Partições vazias têm médias/IC nulos e contagem zero.

Baseline: ranking uniforme de 10.000 milhares sem reposição. Para milhar, `K/10000`. Para sufixos, `1 - produto((10000-M-i)/(10000-i))`, com `i=0..K-1`, `M=10` para centena e `M=100` para dezena. Retorna taxa observada, baseline, diferença e lift. O baseline não depende de uma seed ou de uma simulação ruidosa.

IC de 95%: Wilson para as 12 taxas, inclusive quando não há acertos. São intervalos marginais aproximados; prêmios de uma extração e janelas sobrepostas podem ser dependentes. Não há correção por comparações múltiplas nem inferência de poder preditivo automático. Valores positivos de lift isoladamente não provam vantagem.

## API

Todas as rotas exigem sessão autenticada e permissão `analysis.use`. Os nomes de campos seguem o padrão inglês do projeto.

`POST /api/statistics/milhar-ranking`

```json
{
  "bank": "LT NACIONAL",
  "time": "21:00",
  "prize": 1,
  "cutoff": "2026-09-20T21:00:00",
  "top": 100,
  "model": "C",
  "configuration": {
    "weights": [0.25, 0.15, 0.15, 0.15, 0.10, 0.10, 0.10],
    "windows": [10, 30, 50, 100, 0],
    "windowWeights": [0.30, 0.25, 0.20, 0.15, 0.10],
    "halfLife": 30,
    "alpha": 1,
    "schedulePrior": 50
  }
}
```

`0` em `windows` representa todo o histórico; `prize: null` inclui posições 1–10. A configuração é opcional. Ordem dos pesos: dezena, centena, dígitos, recência, horário, pares, transição. A resposta inclui configuração efetiva, amostra, último timestamp histórico, componentes e evidências de cada candidata.

`POST /api/statistics/milhar-backtest`

```json
{
  "bank": "LT NACIONAL",
  "time": "21:00",
  "prize": 1,
  "start": "2026-01-01T00:00:00",
  "end": "2026-09-20T23:59:59",
  "model": "C",
  "minimumHistory": 100,
  "compareModels": true,
  "ablation": false
}
```

Opcionalmente inclui `configuration` e `optimize`, um array de configurações completas. Sem `optimize`, não há escolha de pesos. `models[].partitions` separa `train`, `validation` e `test`; `all` é exploratório. Cada trial traz os quatro flags de milhar/centena/dezena na ordem 10/20/50/100, rank real e limite histórico efetivo.

`POST /api/statistics/milhar-backtest/{extractionId}/debug`: corpo de ranking, usando `cutoff` exatamente igual ao timestamp do trial, e modelo/configuração efetivamente avaliados. Retorna histórico usado, último resultado por prêmio, Top 100 com evidências e resultado real com ranking/features. A interface reutiliza os filtros do relatório, mesmo que o formulário tenha sido editado depois.

## Dados ausentes e limites

- A base não contém versões históricas de correções ou horário de publicação de cada resultado. O corte protege contra extrações futuras, mas o backtest usa a **versão atual** do histórico. A auditoria é uma reconstrução, não um snapshot imutável da base original.
- O importador atual preenche números curtos com zeros. A base não informa se o original era uma centena ou um prêmio derivado. O padrão é avaliar o 1º prêmio; ao escolher outras posições, considere a natureza dos prêmios da loteria. O motor aceita apenas strings de quatro dígitos ASCII e não altera registros inválidos.
- Banca é correspondência exata, como nos serviços existentes. O campo permite bancas além das duas oferecidas nos outros formulários.
- Limites explícitos: 250.000 resultados lidos, 2.000 timestamps alvo por execução, 12 configurações na busca. Ao ultrapassar, a API rejeita em vez de truncar silenciosamente.
- Uma consulta parametrizada por ranking/backtest; frequências em memória, candidatas decompostas uma vez, features compartilhadas entre modelos/prêmios e nenhuma consulta por candidata. Frequências são reconstruídas por corte/configuração, sem cache entre cortes. Para volumes maiores, uma evolução pode manter contadores incrementais.
- Respostas incluem tempo do cálculo em milissegundos (não inclui consulta ao banco). O cancelamento HTTP propaga-se à consulta e aos loops do motor.

## Verificação

```powershell
dotnet build backend/LotteryLab.Api/LotteryLab.Api.csproj --no-restore
dotnet test backend/LotteryLab.Api.Tests/LotteryLab.Api.Tests.csproj --no-restore
cd frontend
npm run build
node tests/terno-state.cjs
node tests/milhar-statistics-state.cjs
```

Os testes usam dados sintéticos determinísticos e não acessam o banco. Validam sufixos, dígitos, pares, decay, normalização, pesos, ranking, filtros, corte estrito, prêmios simultâneos, baseline, IC, partições e invariância da seleção de configuração quando somente o teste final muda. A homologação com sessão autenticada e PostgreSQL real deve verificar ranking e auditoria de uma extração conhecida.
