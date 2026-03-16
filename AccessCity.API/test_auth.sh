#!/bin/bash

# Configuration
API_URL="http://localhost:8080/api"
EMAIL="testuser@accesscity.com"
PASSWORD="P@ssword123!"
FULL_NAME="Test User"

echo "=== AccessCity Auth Integration Test ==="

# 1. Register a new user
echo -n "1. Registering user... "
REG_RESPONSE=$(curl -s -X POST "$API_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{
    \"email\": \"$EMAIL\",
    \"password\": \"$PASSWORD\",
    \"fullName\": \"$FULL_NAME\"
  }")

if [[ $REG_RESPONSE == *"token"* ]]; then
  echo "✅ Success"
else
  echo "❌ Failed"
  echo "Response: $REG_RESPONSE"
  exit 1
fi

# 2. Login with correct credentials
echo -n "2. Logging in... "
LOGIN_RESPONSE=$(curl -s -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d "{
    \"email\": \"$EMAIL\",
    \"password\": \"$PASSWORD\"
  }")

if [[ $LOGIN_RESPONSE == *"token"* ]]; then
  echo "✅ Success"
  ACCESS_TOKEN=$(echo $LOGIN_RESPONSE | grep -o '"token":"[^"]*' | cut -d'"' -f4)
  REFRESH_TOKEN=$(echo $LOGIN_RESPONSE | grep -o '"refreshToken":"[^"]*' | cut -d'"' -f4)
else
  echo "❌ Failed"
  echo "Response: $LOGIN_RESPONSE"
  exit 1
fi

# 3. Test Token Refresh
echo -n "3. Refreshing token... "
REFRESH_RESPONSE=$(curl -s -X POST "$API_URL/auth/refresh-token?token=$REFRESH_TOKEN")

if [[ $REFRESH_RESPONSE == *"token"* ]]; then
  echo "✅ Success"
else
  echo "❌ Failed"
  echo "Response: $REFRESH_RESPONSE"
fi

# 4. Login with WRONG credentials
echo -n "4. Testing wrong password... "
WRONG_LOGIN=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d "{
    \"email\": \"$EMAIL\",
    \"password\": \"wrong_password\"
  }")

if [ "$WRONG_LOGIN" == "401" ]; then
  echo "✅ Correctly rejected (401)"
else
  echo "❌ Failed (Expected 401, got $WRONG_LOGIN)"
fi

echo "=== Test Completed ==="
