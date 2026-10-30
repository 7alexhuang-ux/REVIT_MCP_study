/**
 * Revit Socket Client
 * Handles WebSocket communication with Revit Plugin
 */

import WebSocket from 'ws';

export interface RevitCommand {
    commandName: string;
    parameters: Record<string, any>;
    requestId?: string;
}

export interface RevitResponse {
    success: boolean;
    data?: any;
    error?: string;
    requestId?: string;
}

// 預設 port 為 8964，可透過環境變數 REVIT_MCP_PORT 覆寫
const DEFAULT_PORT = 8964;
const DEFAULT_COMMAND_TIMEOUT_MS = 300000;
const LONG_COMMAND_TIMEOUT_MS = 600000;

function getConfiguredPort(): number {
    const envPort = process.env.REVIT_MCP_PORT;
    if (envPort) {
        const parsed = parseInt(envPort, 10);
        if (!isNaN(parsed) && parsed >= 1024 && parsed <= 65535) {
            return parsed;
        }
        console.error(`[Socket] Invalid REVIT_MCP_PORT="${envPort}", using default ${DEFAULT_PORT}`);
    }
    return DEFAULT_PORT;
}

export class RevitSocketClient {
    private ws: WebSocket | null = null;
    private host: string = 'localhost';
    private port: number = DEFAULT_PORT;
    private responseHandlers: Map<string, (response: RevitResponse) => void> = new Map();
    private responseRejecters: Map<string, (error: Error) => void> = new Map();
    private connectPromise: Promise<void> | null = null;
    private intentionalDisconnect: boolean = false;

    constructor(host: string = 'localhost', port?: number) {
        this.host = host;
        this.port = port ?? getConfiguredPort();
    }

    /**
     * Connect to Revit Plugin
     */
    async connect(): Promise<void> {
        if (this.isConnected()) {
            return;
        }

        if (this.connectPromise) {
            return this.connectPromise;
        }

        this.intentionalDisconnect = false;
        this.connectPromise = new Promise((resolve, reject) => {
            const wsUrl = `ws://${this.host}:${this.port}`;
            console.error(`[Socket] Connecting to Revit: ${wsUrl}`);

            const socket = new WebSocket(wsUrl);
            this.ws = socket;
            let settled = false;
            const connectionTimeout = setTimeout(() => {
                if (!settled && socket.readyState !== WebSocket.OPEN) {
                    settled = true;
                    this.connectPromise = null;
                    try { socket.terminate(); } catch { /* ignore */ }
                    reject(new Error('Connection Timeout: Please ensure Revit Plugin is running and MCP server is enabled'));
                }
            }, 10000);

            socket.on('open', () => {
                console.error('[Socket] Connected to Revit Plugin');
                settled = true;
                clearTimeout(connectionTimeout);
                this.connectPromise = null;
                resolve();
            });

            socket.on('message', (data: WebSocket.Data) => {
                try {
                    const rawResponse = JSON.parse(data.toString());
                    // Map PascalCase from C# to camelCase for internal use
                    const response: RevitResponse = {
                        success: rawResponse.Success,
                        data: rawResponse.Data,
                        error: rawResponse.Error,
                        requestId: rawResponse.RequestId,
                    };
                    console.error('[Socket] Received response:', response);

                    // Handle Response
                    if (response.requestId) {
                        const handler = this.responseHandlers.get(response.requestId);
                        if (handler) {
                            handler(response);
                            this.responseHandlers.delete(response.requestId);
                            this.responseRejecters.delete(response.requestId);
                        } else {
                            console.error(`[Socket] Received stale or unknown response: ${response.requestId}`);
                        }
                    }
                } catch (error) {
                    console.error('[Socket] Failed to parse message:', error);
                }
            });

            socket.on('error', (error) => {
                console.error('[Socket] WebSocket Error:', error);
                if (!settled) {
                    settled = true;
                    clearTimeout(connectionTimeout);
                    this.connectPromise = null;
                    reject(error);
                }
            });

            socket.on('close', () => {
                console.error('[Socket] Connection closed');
                if (this.ws === socket) {
                    this.ws = null;
                }
                if (!settled) {
                    settled = true;
                    clearTimeout(connectionTimeout);
                    this.connectPromise = null;
                    reject(new Error('Connection closed before opening'));
                }
                if (!this.intentionalDisconnect) {
                    this.rejectPendingResponses(new Error('Connection closed before command response'));
                }
            });
        });

        return this.connectPromise;
    }

    /**
     * Send command to Revit
     */
    async sendCommand(commandName: string, parameters: Record<string, any> = {}): Promise<RevitResponse> {
        if (!this.isConnected()) {
            throw new Error('Not connected to Revit Plugin');
        }

        const requestId = this.generateRequestId();
        const command = {
            CommandName: commandName,
            Parameters: parameters,
            RequestId: requestId,
        };

        console.error(`[Socket] Sending command: ${commandName}`, parameters);

        return new Promise((resolve, reject) => {
            // 已完成的命令要清掉 timer，命令列工具才不會空等十分鐘。
            let timeout: ReturnType<typeof setTimeout> | undefined;
            this.responseHandlers.set(requestId, (response: RevitResponse) => {
                clearTimeout(timeout);
                if (response.success) {
                    resolve(response);
                } else {
                    reject(new Error(response.error || 'Command failed'));
                }
            });
            this.responseRejecters.set(requestId, (error) => {
                clearTimeout(timeout);
                reject(error);
            });

            // 先安裝 timeout，再送出；同步送出失敗也要清理 pending。
            timeout = setTimeout(() => {
                if (this.responseHandlers.has(requestId)) {
                    this.responseHandlers.delete(requestId);
                    this.responseRejecters.delete(requestId);
                    reject(new Error(`Command timed out after ${this.getCommandTimeout(commandName)} ms: ${commandName}`));
                }
            }, this.getCommandTimeout(commandName));
            try {
                this.ws!.send(JSON.stringify(command), (error) => {
                    if (error) {
                        clearTimeout(timeout);
                        this.responseHandlers.delete(requestId);
                        this.responseRejecters.delete(requestId);
                        reject(error);
                    }
                });
            } catch (error) {
                clearTimeout(timeout);
                this.responseHandlers.delete(requestId);
                this.responseRejecters.delete(requestId);
                reject(error);
            }
        });
    }

    /**
     * Check connection status
     */
    isConnected(): boolean {
        return this.ws !== null && this.ws.readyState === WebSocket.OPEN;
    }

    /**
     * Disconnect
     */
    disconnect(): void {
        this.intentionalDisconnect = true;
        this.rejectPendingResponses(new Error('Disconnected before command response'));
        if (this.ws) {
            this.ws.close();
            this.ws = null;
        }
    }

    /**
     * Generate Request ID
     */
    private generateRequestId(): string {
        return `req_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
    }

    getCommandTimeout(commandName: string): number {
        const envTimeout = Number.parseInt(process.env.REVIT_MCP_COMMAND_TIMEOUT_MS ?? '', 10);
        const defaultTimeout = Number.isFinite(envTimeout) && envTimeout > 0
            ? envTimeout
            : DEFAULT_COMMAND_TIMEOUT_MS;
        const longCommands = new Set([
            'create_grading_comparison_sheet',
            'create_grading_presentation',
            'grade_toposolid_to_floors',
            'create_slope_heatmap',
            'create_cutfill_heatmap',
            'export_sheets_pdf',
            'export_walkthrough_package',
            'export_walkthrough_frames',
            'export_model_gltf',
            'export_web_compare',
            'export_design_options',
            'open_document',
            'reload_links',
            'export_walkthrough_depth',
            // 相機管理：Key Plan／總覽／出圖會建立多張視圖並匯出影像，常超過預設逾時
            'run_camera_keyplan',
            'list_cameras',
            'run_camera_overview',
            'create_camera_sheet',
        ]);
        return longCommands.has(commandName) ? Math.max(defaultTimeout, LONG_COMMAND_TIMEOUT_MS) : defaultTimeout;
    }

    private rejectPendingResponses(error: Error): void {
        for (const [requestId, reject] of this.responseRejecters.entries()) {
            this.responseHandlers.delete(requestId);
            this.responseRejecters.delete(requestId);
            reject(error);
        }
    }
}
