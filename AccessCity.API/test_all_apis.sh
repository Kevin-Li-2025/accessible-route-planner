#!/bin/bash
API_URL="http://localhost:5005/api"
EMAIL="testuser_all@accesscity.com"
PASSWORD="P@ssword123!"
FULL_NAME="All API Tester"

echo "=== AccessCity Global API Test ==="

# 1. Register/Login to get Token
echo -n "1. Registering/Logging in... "
LOGIN_RESPONSE=$(curl -s -X POST "$API_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{
    \"email\": \"$EMAIL\",
    \"password\": \"$PASSWORD\",
    \"fullName\": \"$FULL_NAME\"
  }")

if [[ $LOGIN_RESPONSE != *"token"* ]]; then
    # Try login if register fails (user might exist)
    LOGIN_RESPONSE=$(curl -s -X POST "$API_URL/auth/login" \
      -H "Content-Type: application/json" \
      -d "{
        \"email\": \"$EMAIL\",
        \"password\": \"$PASSWORD\"
      }")
fi

if [[ $LOGIN_RESPONSE == *"token"* ]]; then
  echo "Success"
  TOKEN=$(echo $LOGIN_RESPONSE | grep -o '"token":"[^"]*' | cut -d'"' -f4)
else
  echo "Failed to get token"
  echo "Response: $LOGIN_RESPONSE"
  exit 1
fi

# 2. Test Hazards (Public or Auth?)
echo -n "2. Testing GET /api/hazards... "
HAZARDS_RES=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/hazards")
echo "Code: $HAZARDS_RES"

# 3. Test Geocoding (Public)
echo -n "3. Testing GET /api/geocoding/search... "
GEO_RES=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/geocoding/search?query=Birmingham")
echo "Code: $GEO_RES"

# 4. Test Routing (Auth Required)
echo -n "4. Testing POST /api/routing/safe-path (No Auth)... "
ROUTE_NO_AUTH=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_URL/routing/safe-path" \
  -H "Content-Type: application/json" \
  -d '{"start":{"x":-1.9003, "y":52.4814}, "end":{"x":-1.9303, "y":52.4814}}')
echo "Code: $ROUTE_NO_AUTH (Expected 401)"

echo -n "4b. Testing POST /api/routing/safe-path (With Auth)... "
ROUTE_AUTH=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_URL/routing/safe-path" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"start":{"x":-1.9003, "y":52.4814}, "end":{"x":-1.9303, "y":52.4814}}')
echo "Code: $ROUTE_AUTH"

# 5. Test Dashboard
echo -n "5. Testing GET /api/dashboard/summary... "
DASH_RES=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/dashboard/summary")
echo "Code: $DASH_RES"

echo "=== API Test Completed ==="
