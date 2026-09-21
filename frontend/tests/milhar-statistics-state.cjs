const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const ts = require('typescript');
const calls = [];
const storage = new Map();
const http = { post: (url, body) => ({ pipe: cleanup => ({ subscribe: handlers => {
  const call = { url, body, handlers, cancelled: false };
  calls.push(call);
  return { unsubscribe: () => { call.cancelled = true; cleanup(); } };
} }) }) };
const output = ts.transpileModule(fs.readFileSync('src/app/milhar-statistics.component.ts', 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, experimentalDecorators: true }
}).outputText;
const context = { exports: {}, Date, console,
  localStorage: { setItem: (k, v) => storage.set(k, v), getItem: k => storage.get(k) },
  require: name => name === '@angular/core' ? { Component: () => target => target, Input: () => () => {}, inject: () => http }
    : name === 'rxjs' ? { finalize: fn => fn } : {} };
vm.runInNewContext(output, context);
const view = new context.exports.MilharStatisticsComponent(); view.api = '/api';
view.analyze(); assert.equal(calls.length, 1); assert.equal(calls[0].url, '/api/statistics/milhar-ranking');
assert.equal(calls[0].body.prize, 1); assert.equal(calls[0].body.cutoff.endsWith('Z'), false);
view.config.weights[0] = .5;
assert.equal(calls[0].body.configuration.weights[0], .25, 'request must freeze the configuration');
view.analyze(); assert.equal(calls.length, 1); assert.match(view.error, /somem 1/);
view.config.weights[0] = .25; view.optimize = true; view.backtest();
assert.equal(calls[1].body.optimize.length, 3); assert.equal(calls[1].body.optimize[2].halfLife, 50);
const request = calls[1].body;
const winner = { ...request.configuration, halfLife: 50 };
calls[1].handlers.next({ version: 'MILHAR_STATISTICS_1', request, winner, models: [{ model: 'C', configuration: winner, trials: [] }] });
assert.equal(storage.size, 1); view.loadWinner(); assert.equal(view.config.halfLife, 50);
view.bank = 'OTHER'; view.time = '12:00'; view.prize = 5;
view.inspect({ extractionId: 42, date: '2026-01-10T21:00:00', time: '21:00' });
assert.equal(calls[2].body.bank, request.bank, 'audit must use evaluated filters, not edited form');
assert.equal(calls[2].body.prize, request.prize); assert.equal(calls[2].body.configuration.halfLife, 50);
view.cancel(); assert.equal(calls[2].cancelled, true); assert.equal(view.loading, false);
view.optimizationJson = 'invalid'; view.backtest(); assert.equal(calls.length, 3);
console.log('Milhar statistics state regression checks passed.');
