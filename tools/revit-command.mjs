import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { RevitSocketClient } from '../MCP-Server/build/socket.js';
import { registerRevitTools, executeRevitTool } from '../MCP-Server/build/tools/revit-tools.js';

/** 使用正式 MCP bridge 與 dispatcher，JSON 從檔案讀取以免 shell quoting 失真。 */
export async function runCommand(command, parameters, outputFile) {
    if (!registerRevitTools().some(tool => tool.name === command)) throw new Error(`目前 MCP_PROFILE 未註冊工具：${command}`);
    if (!parameters || typeof parameters !== 'object' || Array.isArray(parameters)) throw new Error('參數檔必須是 JSON object');
    const client = new RevitSocketClient('localhost');
    let wireResponse;
    const send = client.sendCommand.bind(client);
    client.sendCommand = async (...args) => { wireResponse = await send(...args); return wireResponse; };
    const startedAt = new Date().toISOString();
    let record;
    try {
        await client.connect();
        record = { Success: true, Data: await executeRevitTool(command, parameters, client), Error: null };
    } catch (error) {
        record = { Success: false, Data: null, Error: error.message || error.code || String(error) };
    } finally { client.disconnect(); }
    // wire RequestId 由 RevitSocketClient 生成及比對；此檔是執行紀錄，不冒充 wire response。
    record = { ...record, RequestId: wireResponse?.requestId ?? null, command, parameters, startedAt, finishedAt: new Date().toISOString() };
    if (outputFile) {
        await fs.mkdir(path.dirname(path.resolve(outputFile)), { recursive: true });
        await fs.writeFile(outputFile, JSON.stringify(record, null, 2));
    }
    return record;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    try {
        const [command, paramsFile, outputFile] = process.argv.slice(2);
        if (!command || !paramsFile) throw new Error('用法：node tools/revit-command.mjs <tool_name> <params.json> [result.json]');
        const parameters = JSON.parse((await fs.readFile(paramsFile, 'utf8')).replace(/^\uFEFF/, ''));
        const result = await runCommand(command, parameters, outputFile);
        console.log(JSON.stringify(result, null, 2));
        if (!result.Success || result.Data?.complete === false) process.exitCode = 1;
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
