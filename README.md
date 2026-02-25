# aerie

aviary home citadel

## Aerie.API

`make run` to run it.

[http://localhost:5197/swagger](http://localhost:5197/swagger) for swagger.

## Dependencies

- Docker
- dotnet 10 sdk
- HomeAssistant instance

## Secrets

Local secrets file: `src/Aerie.Api/.env.json`
Flat KVP JSON file.

Required values:

- `ha_host`: host name/IP of HomeAssistant 
- `ha_port`: HA API port (usually 8123)
- `ha_token`: HA API token
