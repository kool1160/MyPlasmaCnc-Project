# Project Scope

## Product

**MyPlasm Inspector** is a Windows desktop diagnostic tool for the existing Proma MyPlasm CNC controller.

## Stage 1 — Connect and inspect

Deliver a safe application that can:

- enumerate FTDI devices through D2XX;
- identify the `MyPlasm CNC` device;
- display USB/FTDI identity and library information;
- open and close the controller safely;
- capture passive receive data with timestamps;
- export raw and human-readable session evidence;
- operate with no internet connection.

## Stage 2 — Decode useful information

Add only evidence-confirmed decoding for:

- controller identity;
- firmware version;
- machine-reference state;
- coordinates;
- controller inputs;
- plasma-interface state;
- communication errors;
- unknown packet visualization.

Any required request packet must be proven read-only and added to the centralized safety allowlist.

## Stage 3 — Polish and package

Provide:

- clean shop-friendly Windows UI;
- clear read-only mode indication;
- portable build;
- installer;
- structured JSON report;
- raw RX/TX captures;
- readable report;
- SHA-256 evidence manifest;
- automated tests;
- build, use, and capture instructions.

## Out of scope for this project

- replacing the MyPlasm controller;
- controlling machine motion;
- operating the plasma source;
- editing controller configuration;
- changing firmware;
- CAD/CAM, nesting, or production cutting;
- supporting unrelated CNC controllers.

Those may become separate projects only after this inspector is complete and the protocol is understood.

## Success definition

The application reliably identifies and inspects Chris's controller, exports reproducible evidence, and cannot accidentally cause movement, outputs, configuration changes, EEPROM changes, or firmware operations.
