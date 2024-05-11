import { spawn } from "child_process";
import crypto from "crypto";
import net from "net";
import appState from "./appState";
import { writeLog } from "./logger";
import { app } from "electron";

let server: net.Server;

export function launchSidecar(): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (server) {
      reject("Sidecar IPC server has already been created.");
    }

    const pipeName = crypto.randomUUID();
    const serverPipePath = `\\\\.\\pipe\\${pipeName}`;

    writeLog("Starting pipe server at path: ", "Info", serverPipePath);

    const connectTimeout = setTimeout(() => {
      writeLog("Timed out while waiting for sidecar to connect.", "Error");
      reject();
    }, 10000);

    server = net.createServer(function (stream) {
      clearTimeout(connectTimeout);
      writeLog("Sidecar connected to IPC channel.");

      stream.on("data", function (data) {
        const message = JSON.parse(data.toString());
        writeLog("Received message from sidecar: ", "Info", message);
      });

      stream.on("end", function () {
        writeLog("Sidecar pipe server closing.");
        server.close();
      });
      resolve();
    });

    server.on("close", function () {
      writeLog("Sidecar pipe server closed. Exiting.");
      app.exit();
    });

    server.on("error", (e) => {
      writeLog("Error in sidecar pipe server.", "Error", e);
      reject();
    });

    server.listen(serverPipePath);

    let binaryPath =
      process.platform === "win32"
        ? "ControlR.Streamer.Sidecar.exe"
        : "ControlR.Streamer.Sidecar";

    if (!app.isPackaged) {
      binaryPath = `.\\.artifacts\\${binaryPath}`;
    }

    const sidecarProc = spawn(binaryPath, [
      "--streamer-pipe",
      pipeName,
      "--parent-id",
      process.pid.toString(),
    ]);

    if (sidecarProc.exitCode !== null) {
      writeLog("Sidecar failed to start.", "Error");
      reject();
    }
  });
}

type SidecarDtoType = "Unknown" | "DesktopChanged";
