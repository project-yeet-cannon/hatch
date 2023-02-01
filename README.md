# aerie

aviary home citadel

## Aerie.API

`make run` to run it.

[http://localhost:5082/swagger](http://localhost:5082/swagger) for swagger.

## Containerization nonsense

The image is named `aerie-web`.

Launch one with

```bash
docker run -d -p 8080:80 [image-id]
```

The key thing here is to map from port 8080 externally to 80 (where the API is listening).

Swagger will not be available.
