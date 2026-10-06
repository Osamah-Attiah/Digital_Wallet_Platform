.PHONY: init up down logs smoke migrate verify images observability validate scan
init:
	python3 ops/scripts/init-local.py
up: init
	docker compose up --build -d --wait --wait-timeout 240
down:
	docker compose down --remove-orphans
logs:
	docker compose logs --tail 100
smoke:
	python3 ops/scripts/smoke.py
migrate:
	docker compose run --rm migrate
verify:
	docker compose run --rm migrate --verify
images:
	docker compose build
observability:
	docker compose -f compose.yaml -f compose.observability.yaml --profile observability up -d --build --wait --wait-timeout 240
validate:
	python3 ops/scripts/validate-packaging.py
scan:
	python3 ops/scripts/scan.py
