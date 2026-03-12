import React, { useState } from "react";
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  Image,
  ScrollView,
  SafeAreaView,
} from "react-native";

export default function AuthScreen() {
  const [isSignup, setIsSignup] = useState(false);

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView contentContainerStyle={styles.scrollContent} showsVerticalScrollIndicator={false}>
        <View style={styles.container}>
          <View style={styles.topShape} />

          <Image
            source={require("../assets/images/logo.png")}
            style={styles.logo}
            resizeMode="contain"
          />

          <Text style={styles.title}>
            Navigate Your City <Text style={styles.orange}>Safely</Text>
          </Text>

          <Text style={styles.subtitle}>
            Find safer walking routes and stay aware of hazards around you.
          </Text>

          <View style={styles.switchContainer}>
            <TouchableOpacity
              style={[styles.tabButton, !isSignup && styles.activeTab]}
              onPress={() => setIsSignup(false)}
            >
              <Text style={[styles.tabText, !isSignup && styles.activeTabText]}>Log In</Text>
            </TouchableOpacity>

            <TouchableOpacity
              style={[styles.tabButton, isSignup && styles.activeTab]}
              onPress={() => setIsSignup(true)}
            >
              <Text style={[styles.tabText, isSignup && styles.activeTabText]}>Sign up</Text>
            </TouchableOpacity>
          </View>

          <View style={styles.dividerRow}>
            <View style={styles.divider} />
            <Text style={styles.dividerText}>
              {isSignup ? "Sign up with" : "Login with"}
            </Text>
            <View style={styles.divider} />
          </View>

          <View style={styles.socialRow}>
            <TouchableOpacity style={styles.socialButton}>
              <Text style={styles.socialText}>Google</Text>
            </TouchableOpacity>
            <TouchableOpacity style={styles.socialButton}>
              <Text style={styles.socialText}>Facebook</Text>
            </TouchableOpacity>
            <TouchableOpacity style={styles.socialButton}>
              <Text style={styles.socialText}>Apple</Text>
            </TouchableOpacity>
          </View>

          <View style={styles.dividerRow}>
            <View style={styles.divider} />
            <Text style={styles.dividerText}>Or</Text>
            <View style={styles.divider} />
          </View>

          {isSignup ? (
            <>
              <View style={styles.nameRow}>
                <View style={styles.halfWidth}>
                  <Text style={styles.label}>First Name</Text>
                  <TextInput
                    placeholder="Enter first name"
                    placeholderTextColor="#B7B7B7"
                    style={styles.input}
                    accessibilityLabel="First name input"
                  />
                </View>

                <View style={styles.halfWidth}>
                  <Text style={styles.label}>Last Name</Text>
                  <TextInput
                    placeholder="Enter last name"
                    placeholderTextColor="#B7B7B7"
                    style={styles.input}
                    accessibilityLabel="Last name input"
                  />
                </View>
              </View>

              <Text style={styles.label}>Email</Text>
              <TextInput
                placeholder="Please enter your email"
                placeholderTextColor="#B7B7B7"
                style={styles.input}
                accessibilityLabel="Email input"
              />

              <Text style={styles.label}>Set Password</Text>
              <TextInput
                placeholder="**********"
                placeholderTextColor="#B7B7B7"
                secureTextEntry
                style={styles.input}
                accessibilityLabel="Set password input"
              />

              <Text style={styles.label}>Confirm Password</Text>
              <TextInput
                placeholder="**********"
                placeholderTextColor="#B7B7B7"
                secureTextEntry
                style={styles.input}
                accessibilityLabel="Confirm password input"
              />

              <TouchableOpacity style={styles.mainButton}>
                <Text style={styles.mainButtonText}>Sign up</Text>
              </TouchableOpacity>
            </>
          ) : (
            <>
              <Text style={styles.label}>Email</Text>
              <TextInput
                placeholder="Please enter your email"
                placeholderTextColor="#B7B7B7"
                style={styles.input}
                accessibilityLabel="Email input"
              />

              <Text style={styles.label}>Password</Text>
              <TextInput
                placeholder="**********"
                placeholderTextColor="#B7B7B7"
                secureTextEntry
                style={styles.input}
                accessibilityLabel="Password input"
              />

              <TouchableOpacity>
                <Text style={styles.forgotPassword}>Forgot Password?</Text>
              </TouchableOpacity>

              <TouchableOpacity style={styles.mainButton}>
                <Text style={styles.mainButtonText}>Log In</Text>
              </TouchableOpacity>
            </>
          )}
        </View>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F7F7F7",
  },
  scrollContent: {
    flexGrow: 1,
  },
  container: {
    flex: 1,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 24,
    paddingTop: 10,
    paddingBottom: 30,
  },
  topShape: {
    position: "absolute",
    top: 0,
    left: 0,
    width: 170,
    height: 170,
    backgroundColor: "#F1E2D7",
    borderBottomRightRadius: 170,
  },
  logo: {
    width: 130,
    height: 130,
    alignSelf: "center",
    marginTop: 30,
    marginBottom: 10,
  },
  title: {
    fontSize: 22,
    fontWeight: "800",
    textAlign: "center",
    color: "#4C3D3D",
    marginBottom: 8,
  },
  orange: {
    color: "#F28C28",
  },
  subtitle: {
    textAlign: "center",
    color: "#5F5F5F",
    fontSize: 15,
    lineHeight: 22,
    marginBottom: 24,
    paddingHorizontal: 10,
  },
  switchContainer: {
    flexDirection: "row",
    backgroundColor: "#ECECF1",
    borderRadius: 14,
    padding: 4,
    marginBottom: 24,
  },
  tabButton: {
    flex: 1,
    paddingVertical: 14,
    alignItems: "center",
    borderRadius: 12,
  },
  activeTab: {
    backgroundColor: "#FFFFFF",
  },
  tabText: {
    color: "#7B7B8B",
    fontSize: 16,
    fontWeight: "500",
  },
  activeTabText: {
    color: "#1C2452",
    fontWeight: "700",
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 18,
  },
  divider: {
    flex: 1,
    height: 1,
    backgroundColor: "#D7D7D7",
  },
  dividerText: {
    marginHorizontal: 12,
    color: "#7B7B8B",
    fontSize: 16,
    fontWeight: "600",
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 22,
    gap: 10,
  },
  socialButton: {
    flex: 1,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E0E0E0",
    borderRadius: 12,
    paddingVertical: 14,
    alignItems: "center",
    minHeight: 48,
  },
  socialText: {
    fontSize: 16,
    fontWeight: "600",
    color: "#000000",
  },
  nameRow: {
    flexDirection: "row",
    gap: 12,
  },
  halfWidth: {
    flex: 1,
  },
  label: {
    fontSize: 15,
    fontWeight: "700",
    color: "#6B7280",
    marginBottom: 8,
    marginLeft: 4,
  },
  input: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#D9D9D9",
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    marginBottom: 18,
    minHeight: 52,
    color: "#1F2937",
  },
  forgotPassword: {
    textAlign: "right",
    color: "#114B9B",
    fontWeight: "700",
    marginBottom: 24,
  },
  mainButton: {
    backgroundColor: "#215A9A",
    paddingVertical: 16,
    borderRadius: 14,
    alignItems: "center",
    marginTop: 8,
    minHeight: 54,
    shadowColor: "#000",
    shadowOpacity: 0.15,
    shadowRadius: 5,
    elevation: 4,
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 18,
    fontWeight: "700",
  },
});