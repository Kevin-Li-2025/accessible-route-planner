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
import { LinearGradient } from "expo-linear-gradient";
import { Ionicons } from "@expo/vector-icons";
import { Stack } from "expo-router";

export default function AuthScreen() {
  const [isSignup, setIsSignup] = useState(false);

  return (
    <SafeAreaView style={styles.safeArea}>
      <Stack.Screen options={{ headerShown: false }} />
      <ScrollView
        contentContainerStyle={styles.scrollContent}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.container}>
          <View style={styles.topShape} />

          <Image
            source={require("../assets/images/logo.png")}
            style={styles.logo}
            resizeMode="contain"
          />

          <Text style={styles.title}>
            Navigate Your City <Text style={styles.safetyText}>Safely</Text>
          </Text>

          <Text style={styles.subtitle}>
            Find safer walking routes and stay aware of hazards around you.
          </Text>

          <View style={styles.switchContainer}>
            <TouchableOpacity
              style={[styles.tabButton, !isSignup && styles.activeTab]}
              onPress={() => setIsSignup(false)}
            >
              <Text style={[styles.tabText, !isSignup && styles.activeTabText]}>
                Log In
              </Text>
            </TouchableOpacity>

            <TouchableOpacity
              style={[styles.tabButton, isSignup && styles.activeTab]}
              onPress={() => setIsSignup(true)}
            >
              <Text style={[styles.tabText, isSignup && styles.activeTabText]}>
                Sign up
              </Text>
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
              <Image
                source={require("../assets/images/google.png")}
                style={styles.socialIcon}
                resizeMode="contain"
              />
              <Text style={styles.socialText} numberOfLines={1}>
                Google
              </Text>
            </TouchableOpacity>

            <TouchableOpacity style={styles.socialButton}>
              <Image
                source={require("../assets/images/facebook.png")}
                style={styles.socialIcon}
                resizeMode="contain"
              />
              <Text style={styles.socialText} numberOfLines={1}>
                Facebook
              </Text>
            </TouchableOpacity>

            <TouchableOpacity style={styles.socialButton}>
              <Image
                source={require("../assets/images/apple.png")}
                style={styles.socialIcon}
                resizeMode="contain"
              />
              <Text style={styles.socialText} numberOfLines={1}>
                Apple
              </Text>
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
                  <View style={styles.fieldWrapBlue}>
                    <Text style={styles.floatingLabelBlue}>First Name</Text>
                    <TextInput
                      placeholder="Enter first name"
                      placeholderTextColor="#C7C7C7"
                      style={styles.inputBlue}
                    />
                  </View>
                </View>

                <View style={styles.halfWidth}>
                  <View style={styles.fieldWrapBlue}>
                    <Text style={styles.floatingLabelBlue}>Last Name</Text>
                    <TextInput
                      placeholder="Enter last name"
                      placeholderTextColor="#C7C7C7"
                      style={styles.inputBlue}
                    />
                  </View>
                </View>
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Email</Text>
                <TextInput
                  placeholder="Please enter your email"
                  placeholderTextColor="#C7C7C7"
                  style={styles.input}
                />
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Set Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#C7C7C7"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Confirm Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#C7C7C7"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <TouchableOpacity activeOpacity={0.9} style={styles.buttonOuter}>
                <LinearGradient
                  colors={["#2E6AB0", "#215A9A"]}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={styles.mainButton}
                >
                  <Text style={styles.mainButtonText}>Sign up</Text>
                </LinearGradient>
              </TouchableOpacity>
            </>
          ) : (
            <>
              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabelBlue}>Email</Text>
                <TextInput
                  placeholder="Please enter your email"
                  placeholderTextColor="#D0D0D0"
                  style={styles.input}
                  keyboardType="email-address"
                  autoCapitalize="none"
                />
              </View>

              <View style={styles.fieldWrap}>
                <Text style={styles.floatingLabel}>Password</Text>
                <View style={styles.passwordRow}>
                  <TextInput
                    placeholder="**********"
                    placeholderTextColor="#D0D0D0"
                    secureTextEntry
                    style={styles.passwordInput}
                  />
                  <Ionicons name="eye-outline" size={20} color="#B7B7B7" />
                </View>
              </View>

              <TouchableOpacity>
                <Text style={styles.forgotPassword}>Forgot Password?</Text>
              </TouchableOpacity>

              <TouchableOpacity activeOpacity={0.9} style={styles.buttonOuter}>
                <LinearGradient
                  colors={["#2E6AB0", "#215A9A"]}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={styles.mainButton}
                >
                  <Text style={styles.mainButtonText}>Log In</Text>
                </LinearGradient>
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
    paddingTop: 6,
    paddingBottom: 44,
  },
  topShape: {
    position: "absolute",
    top: 0,
    left: 0,
    width: 175,
    height: 175,
    backgroundColor: "#F1E2D7",
    borderBottomRightRadius: 180,
  },
  logo: {
    width: 116,
    height: 116,
    alignSelf: "center",
    marginTop: 34,
    marginBottom: 10,
    backgroundColor: "transparent",
  },
  title: {
    fontSize: 18,
    fontWeight: "800",
    textAlign: "center",
    color: "#4B3D3D",
    marginBottom: 6,
  },
  safetyText: {
    color: "#F59B23",
    textShadowColor: "rgba(33,90,154,0.25)",
    textShadowOffset: { width: 0, height: 1 },
    textShadowRadius: 6,
  },
  subtitle: {
    textAlign: "center",
    color: "#5D5D5D",
    fontSize: 12,
    lineHeight: 18,
    marginBottom: 30,
    paddingHorizontal: 18,
  },
  switchContainer: {
    flexDirection: "row",
    backgroundColor: "#ECECF1",
    borderRadius: 10,
    padding: 4,
    marginBottom: 24,
  },
  tabButton: {
    flex: 1,
    minHeight: 40,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 8,
  },
  activeTab: {
    backgroundColor: "#FFFFFF",
  },
  tabText: {
    color: "#7B7B8B",
    fontSize: 12,
    fontWeight: "500",
  },
  activeTabText: {
    color: "#1C2452",
    fontWeight: "700",
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 16,
    marginTop: 6,
  },
  divider: {
    flex: 1,
    height: 1,
    backgroundColor: "#DADADA",
  },
  dividerText: {
    marginHorizontal: 16,
    color: "#6B7280",
    fontSize: 11,
    fontWeight: "600",
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 8,
    marginBottom: 26,
  },
  socialButton: {
    flex: 1,
    minHeight: 44,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#D8D8D8",
    borderRadius: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    paddingHorizontal: 6,
  },
  socialIcon: {
    width: 18,
    height: 18,
  },
  socialText: {
    flexShrink: 1,
    fontSize: 11,
    fontWeight: "700",
    color: "#111111",
    textAlign: "center",
  },
  nameRow: {
    flexDirection: "row",
    gap: 12,
  },
  halfWidth: {
    flex: 1,
  },
  fieldWrap: {
    position: "relative",
    marginBottom: 16,
  },
  fieldWrapBlue: {
    position: "relative",
    marginBottom: 16,
  },
  floatingLabel: {
    position: "absolute",
    top: -10,
    left: 16,
    zIndex: 2,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 8,
    color: "#9A9A9A",
    fontSize: 11,
    fontWeight: "600",
  },
  floatingLabelBlue: {
    position: "absolute",
    top: -10,
    left: 16,
    zIndex: 2,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 8,
    color: "#114B9B",
    fontSize: 11,
    fontWeight: "700",
  },
  input: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E3E3E3",
    borderRadius: 10,
    paddingHorizontal: 16,
    paddingVertical: 14,
    minHeight: 50,
    color: "#1F2937",
    fontSize: 11,
  },
  inputBlue: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1.5,
    borderColor: "#2E6AB0",
    borderRadius: 10,
    paddingHorizontal: 16,
    paddingVertical: 14,
    minHeight: 50,
    color: "#1F2937",
    fontSize: 11,
  },
  passwordRow: {
    backgroundColor: "#FAFAFA",
    borderWidth: 1,
    borderColor: "#E3E3E3",
    borderRadius: 10,
    paddingHorizontal: 16,
    minHeight: 50,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  passwordInput: {
    flex: 1,
    color: "#1F2937",
    paddingVertical: 14,
    fontSize: 11,
  },
  forgotPassword: {
    textAlign: "right",
    color: "#114B9B",
    fontWeight: "700",
    marginTop: -2,
    marginBottom: 24,
    fontSize: 12,
  },
  buttonOuter: {
    borderRadius: 14,
    shadowColor: "#114B9B",
    shadowOpacity: 0.25,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 4 },
    elevation: 5,
  },
  mainButton: {
    borderRadius: 14,
    minHeight: 52,
    justifyContent: "center",
    alignItems: "center",
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 14,
    fontWeight: "700",
  },
});
