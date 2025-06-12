# How to test

1. Open the [`MongoDb.Examples.sln`](../../MongoDb.Examples.sln) solution.
2. Start the MongoDB container using `docker-compose`.

```bash
	docker-compose up -d
```

3. Run the application (e.g. using Visual Studio, VS Code or Jetbrains Rider).
4. Create a product by sending a `POST` request to `/products` (no JSON body needed, a random product is created on every request).

```bash
	curl --location --request POST 'http://localhost:50000/products'
```

5. Perform a `GET /products` and see what you get in return.

```bash
	curl --location 'http://localhost:50000/products'
```

6. Profit. Go make something cool!